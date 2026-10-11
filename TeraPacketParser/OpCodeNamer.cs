using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Nostrum;

namespace TeraPacketParser;

// Maps between numeric OpCodes and OpCode names
// Since this mapping is version dependent, we can't use a sing global instance of this
public class OpCodeNamer
{
    private Dictionary<string, ushort> _opCodeCodes;
    private Dictionary<ushort, string> _opCodeNames;

    public OpCodeNamer(IEnumerable<KeyValuePair<ushort, string>> names)
    {
        (_opCodeNames, _opCodeCodes) = BuildMaps(names);
    }

    // Some Toolboxes add aliases, a second name for an opcode that already has one (Agaia's has
    // C_CANCEL_SKILL_STRICTLYY next to C_CANCEL_SKILL_STRICTLY): keep the first name and code seen.
    private static (Dictionary<ushort, string>, Dictionary<string, ushort>) BuildMaps(IEnumerable<KeyValuePair<ushort, string>> names)
    {
        var byCode = new Dictionary<ushort, string>();
        var byName = new Dictionary<string, ushort>();
        foreach (var (code, name) in names)
        {
            byCode.TryAdd(code, name);
            byName.TryAdd(name, code);
        }
        return (byCode, byName);
    }

    public OpCodeNamer(string filename) : this(ReadOpCodeFile(filename).Result)
    {
    }

    public string GetName(ushort opCode)
    {
        return _opCodeNames.TryGetValue(opCode, out var name) ? name : opCode.ToString("X4");
    }

    private static async Task<IEnumerable<KeyValuePair<ushort, string>>> ReadOpCodeFile(string filename)
    {
        if (!File.Exists(filename))
        {
            filename = filename.Contains("smt_")
                ? filename.Replace("smt_", "sysmsg.").Replace(".txt", ".map")
                : $"{Path.GetDirectoryName(filename)}/protocol.{Path.GetFileName(filename).Replace(".txt", ".map")}";
        }

        if (!File.Exists(filename)) { return new List<KeyValuePair<ushort, string>>(); }

        await MiscUtils.WaitForFileUnlock(filename, FileAccess.Read);

        var names = File.ReadLines(filename)
            .Select(s => Regex.Replace(s.Replace("=", " "), @"\s+", " ").Split(' ').ToArray())
            .Select(parts => new KeyValuePair<ushort, string>(ushort.Parse(parts[1]), parts[0].Replace("TTB_", ""))) // *sigh*
            .Distinct();
        return names;
    }

    public ushort GetCode(string name)
    {
        if (_opCodeCodes.TryGetValue(name, out var code))
            return code;

        Debug.WriteLine("Missing opcode: " + name);
        return 0;
    }

    public void Reload(uint version, int releaseVersion, string path)
    {
        var p = Path.GetDirectoryName(path) ?? "";
        var filename = $"{p}/sysmsg.{version}.map";
        if (!File.Exists(filename)) filename = $"{p}/sysmsg.{releaseVersion / 100}.map";
        if (!File.Exists(filename)) return;

        (_opCodeNames, _opCodeCodes) = BuildMaps(ReadOpCodeFile(filename).Result);
    }

    public void Add(string name, ushort code)
    {
        _opCodeCodes[name] = code;
        _opCodeNames[code] = name;
    }
}