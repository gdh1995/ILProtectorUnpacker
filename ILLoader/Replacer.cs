using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;

namespace ILLoader
{
    public struct MethodHistory {
        public MethodInfo instance;
        public MethodBody body;
        public long address;
    }
    public class ReplaceMethodAttribute : System.Attribute
    {
        public string Name { get; private set; }

        public ReplaceMethodAttribute(string Name)
        {
            this.Name = Name;
        }

        private static Dictionary<MethodBase, MethodHistory> SharedBodyMap = new Dictionary<MethodBase, MethodHistory>();

        public static ReplaceMethodAttribute Remember(MethodBase newMethod, MethodHistory history)
        {
            var attr = newMethod.GetCustomAttributes(typeof(ReplaceMethodAttribute), false)
                    .FirstOrDefault() as ReplaceMethodAttribute;
            if (attr != null)
                SharedBodyMap.Add(newMethod, history);
            return attr;
        }
        public static MethodHistory GetOldMethod(MethodBase method)
        {
            SharedBodyMap.TryGetValue(method, out var history);
            return history;
        }
    }

    class Replacer
    {


        public static int GetProtectedMethodID(MethodBody methodBody)
        {
            var bodyBytes = methodBody.GetILAsByteArray();
            if (bodyBytes[0] == 0x7e && bodyBytes[4] == 0x04 && bodyBytes[5] == 0x20)
                return BitConverter.ToInt32(bodyBytes, 6);
            return -1;
        }
    }
}
