using System;
using System.Collections.Generic;
using System.Text;

namespace Birko.Data.Attribute
{
    [System.AttributeUsage(System.AttributeTargets.Class | System.AttributeTargets.Struct)]
    public class Table : System.Attribute
    {
        public string Name { get; private set; }

        public Table(string name)
        {
            Name = name;
        }
    }
}
