// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Diagnostics;
using System.IO;
using McMaster.Extensions.CommandLineUtils;
using NUnit.Framework;
using osu.Game.Rulesets.Catch.Tests;
using osu.Game.Rulesets.Mania.Tests;
using osu.Game.Rulesets.Osu.Tests;
using osu.Game.Rulesets.Taiko.Tests;
using osu.Game.Tests.Beatmaps;

namespace PerformanceCalculator
{
    [Command("compare-conversion")]
    public class CompareConversion
    {
        [Argument(0)]
        public string StablePath { get; set; }

        [Argument(1)]
        public string BeatmapPath { get; set; }

        public int OnExecute(CommandLineApplication app, IConsole console)
        {
            Process stableProc = Process.Start(new ProcessStartInfo(StablePath)
            {
                RedirectStandardInput = true,
                RedirectStandardOutput = true
            });

            Console.WriteLine($"file,ruleset,matches");

            if (Directory.Exists(BeatmapPath))
            {
                foreach (var file in Directory.GetFiles(BeatmapPath, "*.osu"))
                    processFile(file, stableProc);
            }
            else
                processFile(BeatmapPath, stableProc);

            return 0;
        }

        private void processFile(string filename, Process stableProc)
        {
            runTest(0, () => new OsuBeatmapConversionTest().TestWithOsuStable(stableProc.StandardInput, stableProc.StandardOutput, filename));
            runTest(1, () => new TaikoBeatmapConversionTest().TestWithOsuStable(stableProc.StandardInput, stableProc.StandardOutput, filename));
            runTest(2, () => new CatchBeatmapConversionTest().TestWithOsuStable(stableProc.StandardInput, stableProc.StandardOutput, filename));
            runTest(3, () => new ManiaBeatmapConversionTest().TestWithOsuStable(stableProc.StandardInput, stableProc.StandardOutput, filename));

            void runTest(int rulesetId, Action action)
            {
                try
                {
                    action();
                    Console.WriteLine($"{Path.GetFileNameWithoutExtension(filename)},{rulesetId},true");
                }
                catch
                {
                    Console.WriteLine($"{Path.GetFileNameWithoutExtension(filename)},{rulesetId},false");
                }
            }
        }
    }
}
