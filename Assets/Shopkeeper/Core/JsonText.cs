using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Shopkeeper.Core
{
    public static class JsonText
    {
        /// <summary>Indent if it parses as JSON; otherwise return the text untouched (it's evidence, don't "fix" it).</summary>
        public static string PrettyOrRaw(string s)
        {
            if (string.IsNullOrWhiteSpace(s)) return s ?? "";
            try { return JToken.Parse(s).ToString(Formatting.Indented); }
            catch { return s; }
        }
    }
}
