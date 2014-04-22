using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace WebApiThrottle
{
    public static class ThrottlePolicyTypeValidator
    {
        static int[] _values = null;

        public static void EnsureValid(int value)
        {
            if (_values == null)
                _values = (int[])Enum.GetValues(typeof(ThrottlePolicyType));

            if (!_values.Contains(value))
                throw new Exception(string.Format("Invalid ThrottlePolicyType value: {0}", value));
        }
    }
}
