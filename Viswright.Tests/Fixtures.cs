using System;
using System.Collections.Generic;
using System.IO;
using Xunit;

// Log.Sink, Handoff.Root and SourceKitConcurrency are process-wide, so tests that set them cannot
// run alongside each other.
[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace Viswright.Tests
{
    /// <summary>A temporary folder holding a map, with the tool's output captured, removed afterwards.</summary>
    public sealed class Workspace : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "viswright-test-" + Guid.NewGuid().ToString("N"));
        public List<string> Output { get; } = [];

        private readonly Action<string> originalSink = Log.Sink;
        private readonly string originalHandoff = Handoff.Root;

        public Workspace()
        {
            Directory.CreateDirectory(Path.Combine(Root, "maps"));
            Log.Sink = Output.Add;
            Handoff.Root = Path.Combine(Root, "handoff");
        }

        public string Write(string relative, string text)
        {
            string path = Path.Combine(Root, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, text.Replace("\r\n", "\n").Replace("\n", "\r\n"));
            return path;
        }

        public void Dispose()
        {
            Log.Sink = originalSink;
            Handoff.Root = originalHandoff;
            try { Directory.Delete(Root, recursive: true); } catch (IOException) { }
        }

        public const string Room = """
            versioninfo
            {
            	"editorversion" "400"
            }
            world
            {
            	"id" "1"
            	"classname" "worldspawn"
            	solid
            	{
            		"id" "2"
            		side
            		{
            			"id" "10"
            			"plane" "(0 64 64) (64 64 64) (64 0 64)"
            			"material" "DEV/DEV_MEASUREWALL01A"
            		}
            		side
            		{
            			"id" "11"
            			"plane" "(0 0 0) (64 0 0) (64 64 0)"
            			"material" "DEV/DEV_MEASUREWALL01A"
            		}
            		side
            		{
            			"id" "12"
            			"plane" "(0 64 64) (0 0 64) (0 0 0)"
            			"material" "DEV/DEV_MEASUREWALL01A"
            		}
            		side
            		{
            			"id" "13"
            			"plane" "(64 64 0) (64 0 0) (64 0 64)"
            			"material" "DEV/DEV_MEASUREWALL01A"
            		}
            		side
            		{
            			"id" "14"
            			"plane" "(64 64 64) (0 64 64) (0 64 0)"
            			"material" "DEV/DEV_MEASUREWALL01A"
            		}
            		side
            		{
            			"id" "15"
            			"plane" "(64 0 0) (0 0 0) (0 0 64)"
            			"material" "DEV/DEV_MEASUREWALL01A"
            		}
            	}
            }
            entity
            {
            	"id" "20"
            	"classname" "func_instance"
            	"file" "instances/door.vmf"
            	"origin" "0 0 0"
            }

            """;
    }
}
