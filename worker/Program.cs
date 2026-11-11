// SoloStone engine worker: JSON lines on stdin/stdout. One request per line: {"id":..,"cmd":..,...}.
// Reply: {"id":..,"ok":true,"result":..} or {"id":..,"ok":false,"error":..}. Anything the engine prints goes to stderr.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace SoloStoneMcp
{
    public static class Program
    {
        public static int Main()
        {
            var stdout = new StreamWriter(Console.OpenStandardOutput()) { AutoFlush = true };
            Console.SetOut(Console.Error); // engine Console.WriteLine must not corrupt the protocol
            var stdin = new StreamReader(Console.OpenStandardInput());
            var worker = new Worker();
            string line;
            while ((line = stdin.ReadLine()) != null)
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                JsonNode id = null;
                JsonObject reply;
                try
                {
                    var req = JsonNode.Parse(line).AsObject();
                    id = req["id"]?.DeepClone();
                    string cmd = (string)req["cmd"];
                    if (cmd == "quit") break;
                    reply = new JsonObject { ["id"] = id, ["ok"] = true, ["result"] = worker.Handle(cmd, req) };
                }
                catch (Exception e)
                {
                    var inner = e is System.Reflection.TargetInvocationException t && t.InnerException != null ? t.InnerException : e;
                    reply = new JsonObject { ["id"] = id, ["ok"] = false, ["error"] = inner.GetType().Name + ": " + inner.Message, ["trace"] = Trim(inner.StackTrace) };
                }
                stdout.WriteLine(reply.ToJsonString());
            }
            return 0;
        }

        internal static string Trim(string trace)
        {
            if (trace == null) return null;
            var lines = trace.Split('\n').Take(12);
            return string.Join("\n", lines);
        }
    }

    public sealed class UserError : Exception { public UserError(string m) : base(m) { } }
}
