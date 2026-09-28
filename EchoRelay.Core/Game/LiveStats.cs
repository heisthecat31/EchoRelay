using Newtonsoft.Json.Linq;

namespace EchoRelay.Core.Game
{
    /// <summary>
    /// Stats for the Echo VR live build only (the lobby builds don't report any). A server profile update carries
    /// stats as {"op", "val", "cnt"} objects, where "op" (add, rep, min, max or avg) says how the value combines with the
    /// stored one, e.g. {"Goals": {"op": "add", "val": 2, "cnt": 1}} adds two goals to the player's total.
    /// </summary>
    public static class LiveStats
    {
        /// <summary>
        /// Returns a copy of an update with each stat already combined with the stored one (by its op), so a plain
        /// merge into the stored profile then keeps totals instead of overwriting them with one match's values.
        /// </summary>
        /// <param name="stored">The stored profile data.</param>
        /// <param name="update">The profile update.</param>
        /// <returns>The update, with its stats combined.</returns>
        public static JObject CombineStats(JObject stored, JObject update)
        {
            JObject result = (JObject)update.DeepClone();
            CombineInto(stored, result);
            return result;
        }

        private static void CombineInto(JObject stored, JObject update)
        {
            foreach (JProperty property in update.Properties())
            {
                if (property.Value is not JObject incoming || stored[property.Name] is not JObject existing)
                    continue;
                if (IsStat(incoming))
                {
                    if (IsStat(existing))
                        property.Value = Combine(existing, incoming);
                }
                else
                {
                    CombineInto(existing, incoming);
                }
            }
        }

        /// <summary>
        /// Whether a JSON object is a stat ({"op", "val", ...}).
        /// </summary>
        private static bool IsStat(JObject value)
            => value["op"]?.Type == JTokenType.String && value["val"] is JValue val && (val.Type == JTokenType.Integer || val.Type == JTokenType.Float);

        /// <summary>
        /// Combines a stored stat with a reported one by the reported op.
        /// </summary>
        private static JObject Combine(JObject stored, JObject update)
        {
            string op = update.Value<string>("op") ?? "rep";
            bool integer = stored["val"]!.Type == JTokenType.Integer && update["val"]!.Type == JTokenType.Integer;
            double storedVal = stored.Value<double>("val"), updateVal = update.Value<double>("val");
            long storedCnt = stored["cnt"]?.Type == JTokenType.Integer ? stored.Value<long>("cnt") : 0;
            long updateCnt = update["cnt"]?.Type == JTokenType.Integer ? update.Value<long>("cnt") : 1;

            double val;
            long cnt;
            switch (op)
            {
                case "add":
                    val = storedVal + updateVal;
                    cnt = storedCnt + updateCnt;
                    break;
                case "max":
                    val = Math.Max(storedVal, updateVal);
                    cnt = storedCnt + updateCnt;
                    break;
                case "min":
                    val = Math.Min(storedVal, updateVal);
                    cnt = storedCnt + updateCnt;
                    break;
                case "avg":
                    cnt = storedCnt + updateCnt;
                    val = cnt > 0 ? (storedVal * storedCnt + updateVal * updateCnt) / cnt : updateVal;
                    integer = false;
                    break;
                default: // "rep"
                    return (JObject)update.DeepClone();
            }

            JObject result = (JObject)update.DeepClone();
            result["val"] = integer && val == Math.Floor(val) ? new JValue((long)val) : new JValue(val);
            result["cnt"] = cnt;
            return result;
        }
    }
}
