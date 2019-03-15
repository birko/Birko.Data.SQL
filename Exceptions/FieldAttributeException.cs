using System;
using System.Collections.Generic;
using System.Text;

namespace Birko.Data.Exceptions
{
    public class FieldAttributeException : Exception
    {
        public FieldAttributeException(string message) : this(message, null) {}
        public FieldAttributeException(string message, Exception innerException) : base(message, innerException) { }
    }
}
