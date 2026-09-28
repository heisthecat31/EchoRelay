using Newtonsoft.Json.Linq;

namespace EchoRelay.Core.Game
{
    /// <summary>
    /// Merges the lobby builds' server profile updates. A game server reports a player's stats at the end of a match as
    /// {"op", "val", "cnt"} objects, where "op" (add, rep, min, max or avg) says how the value combines with the stored
    /// one, e.g. {"Goals": {"op": "add", "val": 2, "cnt": 1}} adds two goals to the player's total.
    /// </summary>
    public static class SummerStats
    {
        /// <summary>
        /// Merges an update into a stored server profile: stats are combined by their op, other objects are merged
        /// recursively, and anything else (arrays, plain values) replaces the stored value.
        /// </summary>
        /// <param name="stored">The stored profile data, updated in place.</param>
        /// <param name="update">The update from the game server.</param>
        public static void Merge(JObject stored, JObject update)
        {
            foreach (JProperty property in update.Properties())
            {
                JToken? current = stored[property.Name];
                if (property.Value is JObject incoming)
                {
                    if (IsStat(incoming))
                        stored[property.Name] = current is JObject existing && IsStat(existing) ? Combine(existing, incoming) : incoming.DeepClone();
                    else if (current is JObject existing)
                        Merge(existing, incoming);
                    else
                        stored[property.Name] = incoming.DeepClone();
                }
                else
                {
                    stored[property.Name] = property.Value.DeepClone();
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
