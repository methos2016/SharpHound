﻿using LiteDB;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace SharpHound.BaseClasses
{
    public class User : DBObject
    {
        public List<string> ServicePrincipalName { get; set; }
    }
}