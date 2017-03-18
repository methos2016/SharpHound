﻿using System;
using System.Collections.Generic;
using System.DirectoryServices;
using System.Linq;
using System.Text;

namespace ExtensionMethods
{
    public static class MyExtensions
    {
        public static string GetProp(this SearchResult result, string prop)
        {
            if (result.Properties[prop].Count == 0)
            {
                return null;
            }else
            {
                return result.Properties[prop][0].ToString();
            }
        }

        public static List<string> GetPropArray(this SearchResult result, string prop)
        {
            if (result.Properties[prop].Count == 0)
            {
                return null;
            }
            else
            {
                List<string> l = new List<string>();
                foreach (var x in result.Properties[prop])
                {
                    l.Add(x.ToString());
                }

                return l;
            }
        }

        public static byte[] GetPropBytes(this SearchResult result, string prop)
        {
            if (result.Properties[prop].Count == 0)
            {
                return null;
            }
            else
            {
                return (byte[])result.Properties[prop][0];
            }
        }
    }
}