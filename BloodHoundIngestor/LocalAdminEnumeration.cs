﻿using ExtensionMethods;
using SharpHound.Exceptions;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.DirectoryServices;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace SharpHound
{
    class LocalAdminEnumeration
    {
        private Helpers Helpers;
        private Options options;

        public LocalAdminEnumeration()
        {
            Helpers = Helpers.Instance;
            options = Helpers.Options;
        }

        public void EnumerateLocalAdmins()
        {
            Console.WriteLine("Starting Local Admin Enumeration");
            List<string> Domains = Helpers.GetDomainList();

            Writer w = new Writer();
            Thread write = new Thread(unused => w.Write());
            write.Start();

            Stopwatch watch = Stopwatch.StartNew();
            foreach (String DomainName in Domains)
            {
                EnumerationData.Reset();
                EnumerationData.DomainSID = Helpers.GetDomainSid(DomainName);

                if (options.Stealth)
                {
                    EnumerateGPOAdmin(DomainName);
                }else
                {
                    ManualResetEvent[] doneEvents = new ManualResetEvent[options.Threads];
                    for (int i = 0; i < options.Threads; i++)
                    {
                        doneEvents[i] = new ManualResetEvent(false);
                        Enumerator e = new Enumerator(doneEvents[i]);
                        Thread consumer = new Thread(unused => e.ThreadCallback());
                        consumer.Start();
                    }

                    System.Timers.Timer t = new System.Timers.Timer();
                    t.Elapsed += new System.Timers.ElapsedEventHandler(Timer_Tick);

                    t.Interval = options.Interval;
                    t.Enabled = true;

                    PrintStatus();

                    int lTotal = 0;

                    DirectorySearcher searcher = Helpers.GetDomainSearcher(DomainName);
                    searcher.Filter = "(sAMAccountType=805306369)";
                    searcher.PropertiesToLoad.Add("dnshostname");
                    foreach (SearchResult x in searcher.FindAll())
                    {
                        EnumerationData.SearchResults.Enqueue(x);
                        lTotal += 1;
                    }
                    searcher.Dispose();

                    EnumerationData.total = lTotal;
                    EnumerationData.SearchResults.Enqueue(null);

                    WaitHandle.WaitAll(doneEvents);
                    t.Dispose();
                }
                
                Console.WriteLine(String.Format("Done local admin enumeration for domain {0} with {1} successful hosts out of {2} queried", DomainName, EnumerationData.live, EnumerationData.done));
            }
            watch.Stop();
            Console.WriteLine("Completed Local Admin Enumeration in " + watch.Elapsed);
            EnumerationData.EnumResults.Enqueue(null);
            write.Join();
        }

        private void Timer_Tick(object sender, System.Timers.ElapsedEventArgs args)
        {
            PrintStatus();
        }

        private void PrintStatus()
        {
            string tot = EnumerationData.total == 0 ? "unknown" : EnumerationData.total.ToString();
            Console.WriteLine(string.Format("Objects Enumerated: {0} out of {1}", EnumerationData.done, tot));
        }

        public class EnumerationData
        {
            public static string DomainSID { get; set; }
            public static ConcurrentQueue<SearchResult> SearchResults;
            public static ConcurrentQueue<LocalAdminInfo> EnumResults = new ConcurrentQueue<LocalAdminInfo>();
            public static int live = 0;
            public static int done = 0;
            public static int total = 0;

            public static void Reset()
            {
                SearchResults = new ConcurrentQueue<SearchResult>();
                live = 0;
                done = 0;
                total = 0;
            }
        }

        private void EnumerateGPOAdmin(string DomainName)
        {
            string targetsid = "S-1-5-32-544__Members";

            Console.WriteLine("Starting GPO Correlation");

            DirectorySearcher gposearcher = Helpers.GetDomainSearcher(DomainName);
            gposearcher.Filter = "(&(objectCategory=groupPolicyContainer)(name=*)(gpcfilesyspath=*))";
            gposearcher.PropertiesToLoad.AddRange(new string[] { "displayname", "name", "gpcfilesyspath" });

            ConcurrentQueue<string> INIResults = new ConcurrentQueue<string>();

            Parallel.ForEach(gposearcher.FindAll().Cast<SearchResult>().ToArray(), (result) =>
            {
                string display = result.GetProp("displayname");
                string name = result.GetProp("name");
                string path  = result.GetProp("gpcfilesyspath");

                if (display == null || name == null || path == null)
                {
                    return;
                }

                string template = String.Format("{0}\\{1}", path, "MACHINE\\Microsoft\\Windows NT\\SecEdit\\GptTmpl.inf");
                
                using (StreamReader sr = new StreamReader(template))
                {
                    string line = String.Empty;
                    string currsection = String.Empty;
                    while ((line = sr.ReadLine()) != null)
                    {
                        Match section = Regex.Match(line, @"^\[(.+)\]");
                        if (section.Success)
                        {
                            currsection = section.Captures[0].Value.Trim();
                        }
                        
                        if (!currsection.Equals("[Group Membership]"))
                        {
                            continue;
                        }

                        Match key = Regex.Match(line, @"(.+?)\s*=(.*)");
                        if (key.Success)
                        {
                            string n = key.Groups[1].Value;
                            string v = key.Groups[2].Value;
                            if (n.Contains(targetsid))
                            {
                                v = v.Trim();
                                List<String> members = v.Split(',').ToList();
                                List<string> resolved = new List<string>();
                                for (int i = 0; i < members.Count; i++)
                                {
                                    string m = members[i];
                                    m = m.Trim('*');

                                    string sid;
                                    if (!m.StartsWith("S-1-"))
                                    {
                                        try
                                        {
                                            sid = new System.Security.Principal.NTAccount(DomainName, m).Translate(typeof(System.Security.Principal.SecurityIdentifier)).Value;
                                        }
                                        catch
                                        {
                                            sid = null;
                                        }
                                    }
                                    else
                                    {
                                        sid = m;
                                    }
                                    if (sid == null)
                                    {
                                        continue;
                                    }
                                    string converted = Helpers.ConvertSIDToName(sid);
                                    if (converted != null)
                                    {
                                        resolved.Add(converted);
                                    }
                                }
                                DirectorySearcher OUSearch = Helpers.GetDomainSearcher(DomainName);
                                
                                OUSearch.Filter = string.Format("(&(objectCategory=organizationalUnit)(name=*)(gplink=*{0}*))", name);
                                foreach (SearchResult r in OUSearch.FindAll())
                                {
                                    DirectorySearcher compsearcher = Helpers.GetDomainSearcher(DomainName, ADSPath: r.GetProp("adspath"));
                                    foreach (SearchResult ra in compsearcher.FindAll())
                                    {
                                        EnumerationData.EnumResults.Enqueue(new LocalAdminInfo
                                        {
                                            objectname = conver
                                        })
                                    }
                                }
                            }
                        }
                    }
                }
            });

            Console.WriteLine("Done GPO Correlation");
        }

        private class Enumerator : EnumeratorBase
        {
            public Enumerator(ManualResetEvent doneEvent) : base(doneEvent)
            {
            }

            public override void ThreadCallback()
            {
                while (true)
                {
                    SearchResult result;
                    if (EnumerationData.SearchResults.TryDequeue(out result))
                    {
                        if (result == null)
                        {
                            EnumerationData.SearchResults.Enqueue(result);
                            break;
                        }
                        try
                        {
                            EnumerateResult(result);
                        }
                        catch (Exception ex)
                        {
                            Console.WriteLine(ex);
                        }

                    }
                }
                _doneEvent.Set();
            }

            private void EnumerateResult(SearchResult result)
            {
                var y = result.Properties["dnshostname"];
                string hostname = result.GetProp("dnshostname"); ;
                if (hostname == null)
                {
                    return;
                }

                if (!_helpers.PingHost(hostname))
                {
                    Interlocked.Increment(ref EnumerationData.done);
                    return;
                }

                List<LocalAdminInfo> results = new List<LocalAdminInfo>();

                try
                {
                    results = LocalGroupAPI(hostname, "Administrators", EnumerationData.DomainSID);
                }catch (SystemDownException)
                {
                    Interlocked.Increment(ref EnumerationData.done);
                    return;
                }catch (APIFailedException)
                {
                    try
                    {
                        results = LocalGroupWinNT(hostname, "Administrators");
                    }
                    catch
                    {
                        Interlocked.Increment(ref EnumerationData.done);
                        return;
                    }
                }catch (Exception e){
                    Console.WriteLine("Exception in local admin enum");
                    Console.WriteLine(e);
                }
                Interlocked.Increment(ref EnumerationData.live);
                Interlocked.Increment(ref EnumerationData.done);

                foreach (LocalAdminInfo r in results)
                {
                    EnumerationData.EnumResults.Enqueue(r);
                }
            }

            #region Helpers
            private List<LocalAdminInfo> LocalGroupWinNT(string Target, string Group)
            {
                DirectoryEntry members = new DirectoryEntry(String.Format("WinNT://{0}/{1},group", Target, Group));
                List<LocalAdminInfo> users = new List<LocalAdminInfo>();
                string servername = Target.Split('.')[0].ToUpper();
                foreach (object member in (System.Collections.IEnumerable)members.Invoke("Members"))
                {
                    using (DirectoryEntry m = new DirectoryEntry(member))
                    {
                        string path = m.Path.Replace("WinNT://", "");
                        if (Regex.Matches(path, "/").Count == 1)
                        {
                            string ObjectName = path.Replace("/", "\\");
                            string ObjectType;

                            if (ObjectName.EndsWith("$"))
                            {
                                ObjectType = "computer";
                            }
                            else
                            {
                                ObjectType = m.SchemaClassName;
                            }

                            string domain = ObjectName.Split('\\')[0];
                            string username = ObjectName.Split('\\')[1];

                            if (domain.ToUpper().Equals(servername))
                            {
                                continue;
                            }
                            string membername = string.Format("{0}@{1}", username, _helpers.GetDomain(domain).Name);

                            users.Add(new LocalAdminInfo
                            {
                                server = Target,
                                objectname = membername,
                                objecttype = ObjectType
                            });
                        }
                    }
                }

                return users;
            }

            private List<LocalAdminInfo> LocalGroupAPI(string Target, string Group, string DomainSID)
            {
                int QueryLevel = 2;
                IntPtr PtrInfo = IntPtr.Zero;
                int EntriesRead = 0;
                int TotalRead = 0;
                IntPtr ResumeHandle = IntPtr.Zero;
                string MachineSID = "DUMMYSTRING";

                Type LMI2 = typeof(LOCALGROUP_MEMBERS_INFO_2);

                List<LocalAdminInfo> users = new List<LocalAdminInfo>();

                int val = NetLocalGroupGetMembers(Target, Group, QueryLevel, out PtrInfo, -1, out EntriesRead, out TotalRead, ResumeHandle);
                if (val == 1722)
                {
                    throw new SystemDownException();
                }

                if (val != 0)
                {
                    throw new APIFailedException();
                }
                
                if (EntriesRead > 0)
                {
                    IntPtr iter = PtrInfo;
                    List<LOCALGROUP_MEMBERS_INFO_2> list = new List<LOCALGROUP_MEMBERS_INFO_2>();
                    for (int i = 0; i < EntriesRead; i++)
                    {
                        LOCALGROUP_MEMBERS_INFO_2 data = (LOCALGROUP_MEMBERS_INFO_2)Marshal.PtrToStructure(iter, LMI2);
                        iter = (IntPtr) (iter.ToInt64() + Marshal.SizeOf(LMI2));
                        list.Add(data);
                    }

                    NetApiBufferFree(PtrInfo);

                    foreach (LOCALGROUP_MEMBERS_INFO_2 data in list)
                    {
                        string s;
                        ConvertSidToStringSid(data.lgrmi2_sid, out s);
                        if (s.EndsWith("-500") && !(s.StartsWith(EnumerationData.DomainSID)))
                        {
                            MachineSID = s.Substring(0, s.LastIndexOf("-"));
                            break;
                        }
                    }

                    foreach (LOCALGROUP_MEMBERS_INFO_2 data in list)
                    {
                        string ObjectType;
                        string ObjectName = data.lgrmi2_domainandname;
                        if (!ObjectName.Contains("\\"))
                        {
                            continue;
                        }
                        if (ObjectName.Split('\\')[1].Equals(""))
                        {
                            continue;
                        }

                        switch (data.lgrmi2_sidusage)
                        {
                            case (SID_NAME_USE.SidTypeUser):
                                ObjectType = "user";
                                break;
                            case (SID_NAME_USE.SidTypeComputer):
                                ObjectType = "computer";
                                break;
                            case (SID_NAME_USE.SidTypeGroup):
                                ObjectType = "group";
                                break;
                            default:
                                ObjectType = "group";
                                break;
                        }

                        string ObjectSID;
                        ConvertSidToStringSid(data.lgrmi2_sid, out ObjectSID);

                        if (ObjectSID.StartsWith(MachineSID))
                        {
                            continue;
                        }

                        string domain = ObjectName.Split('\\')[0];

                        if (domain.ToUpper().Equals("NT AUTHORITY"))
                        {
                            continue;
                        }

                        string username = ObjectName.Split('\\')[1];
                        string membername = string.Format("{0}@{1}", username, _helpers.GetDomain(domain).Name);
                        users.Add(new LocalAdminInfo
                        {
                            server = Target,
                            objectname = membername,
                            sid = ObjectSID,
                            objecttype = ObjectType
                        });
                    }
                }
                return users;
            }
            #endregion

            #region pinvoke-imports
            [DllImport("NetAPI32.dll", CharSet = CharSet.Unicode)]
            public extern static int NetLocalGroupGetMembers(
                [MarshalAs(UnmanagedType.LPWStr)] string servername,
                [MarshalAs(UnmanagedType.LPWStr)] string localgroupname,
                int level,
                out IntPtr bufptr,
                int prefmaxlen,
                out int entriesread,
                out int totalentries,
                IntPtr resume_handle);

            [DllImport("Netapi32.dll", SetLastError = true)]
            static extern int NetApiBufferFree(IntPtr Buffer);

            [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
            public struct LOCALGROUP_MEMBERS_INFO_2
            {
                public IntPtr lgrmi2_sid;
                public SID_NAME_USE lgrmi2_sidusage;
                [MarshalAs(UnmanagedType.LPWStr)]
                public string lgrmi2_domainandname;
            }

            public enum SID_NAME_USE
            {
                SidTypeUser = 1,
                SidTypeGroup,
                SidTypeDomain,
                SidTypeAlias,
                SidTypeWellKnownGroup,
                SidTypeDeletedAccount,
                SidTypeInvalid,
                SidTypeUnknown,
                SidTypeComputer
            }

            [DllImport("advapi32", CharSet = CharSet.Auto, SetLastError = true)]
            static extern bool ConvertSidToStringSid(IntPtr pSid, out string strSid);

            [DllImport("kernel32.dll", SetLastError = true)]
            static extern IntPtr LocalFree(IntPtr hMem);
            #endregion
        }

        public class Writer : WriterBase
        {
            public Writer() : base()
            {
            }

            public override void Write()
            {
                if (_options.URI == null)
                {
                    using (StreamWriter writer = new StreamWriter(_options.GetFilePath("local_admins.csv")))
                    {
                        writer.WriteLine("ComputerName, AccountName, AccountType");
                        while (true)
                        {
                            while (EnumerationData.EnumResults.IsEmpty)
                            {
                                Thread.Sleep(100);
                            }

                            try
                            {
                                LocalAdminInfo info;

                                if (EnumerationData.EnumResults.TryDequeue(out info))
                                {
                                    if (info == null)
                                    {
                                        writer.Flush();
                                        break;
                                    }
                                    writer.WriteLine(info.ToCSV());

                                    _localCount++;
                                    if (_localCount % 1000 == 0)
                                    {
                                        writer.Flush();
                                    }
                                }
                            }
                            catch
                            {
                                continue;
                            }
                        }
                    }
                }
            }
        }
    }
}
