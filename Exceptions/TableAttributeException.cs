using System;
using System.Collections.Generic;
using System.Text;

namespace Birko.Data.Exceptions
{
    public class TableAttributeException : Exception
    {
        public TableAttributeException(string message) : this(message, null) { }
        public TableAttributeException(string message, Exception innerException) : base(message, innerException) { }
    }
}
