using System;
using System.IO;
using CompilePalX.Compiling;

namespace CompilePalX.Compilers
{
    /// <summary>
    /// hlfix converts a .rmf to a .map. If the selected file is already a .map (TrenchBroom
    /// exports/saves directly as .map, no .rmf involved), there's nothing to convert - and
    /// running hlfix anyway fails with "input file can't be the same as output file", since
    /// $goldMapFile$ (the intended output) is identical to the input in that case. Skip it
    /// automatically instead of dumping that error and hlfix's usage text into the log every
    /// compile; HLCSG onward already reads from $goldMapFile$, which is just the original file
    /// when there was nothing for HLFIX to do.
    /// </summary>
    class HlfixProcess : CompileExecutable
    {
        public HlfixProcess() : base("HLFIX") { }

        public override bool CanRun(CompileContext context)
        {
            if (!base.CanRun(context))
                return false;

            if (string.Equals(Path.GetExtension(context.MapFile), ".map", StringComparison.OrdinalIgnoreCase))
            {
                CompilePalLogger.LogLine("Skipping HLFIX: input is already a .map file, nothing to convert.");
                return false;
            }

            return true;
        }
    }
}
