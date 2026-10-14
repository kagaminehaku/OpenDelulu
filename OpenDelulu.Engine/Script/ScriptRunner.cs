using OpenDelulu.Formats.Script;

namespace OpenDelulu.Engine.Script;

/// <summary>What the script waits for next.</summary>
public abstract record ScriptEvent;

/// <summary>A line of dialogue; advance with <see cref="ScriptRunner.Next"/>.</summary>
/// <param name="Speaker">Raw speaker name from the script (Shift-JIS, full-width spaces trimmed).</param>
/// <param name="Voice">Voice file (e.g. pe02_01_01_01_01.ogg) or null for silent lines.</param>
public sealed record ScriptMessage(string Speaker, string? Voice, IReadOnlyList<string> Lines) : ScriptEvent
{
    public string Text => string.Join(Environment.NewLine, Lines);
}

/// <summary>A menu; answer with <see cref="ScriptRunner.Choose"/>.</summary>
public sealed record ScriptChoice(IReadOnlyList<string> Options) : ScriptEvent;

/// <summary>The script finished (End command or the end of the scene).</summary>
public sealed record ScriptEnded : ScriptEvent;

/// <summary>
/// Runs .sdt scripts: walks the command lists, follows Goto/Choice/Switch blocks and stops at each message or menu.
/// Game-specific values (intimacy for Switch) come from <see cref="QuerySubject"/>.
/// </summary>
public sealed class ScriptRunner(Func<string, SdtFile?> load)
{
    const int MaxStepsPerEvent = 100_000;

    readonly Stack<(IReadOnlyList<SdtCommand> Commands, int Index)> _stack = new();
    ScriptChoice? _pendingChoice;
    SdtCommand? _choiceCommand;

    public SdtFile? File { get; private set; }
    public int SceneId { get; private set; }

    /// <summary>Script variables set by SetVar (20 = selected girl, 30 = intimacy setting in the lobby menu).</summary>
    public Dictionary<int, int> Variables { get; } = [];

    /// <summary>Value a Switch tests for its subject (the girl's intimacy level 1..5). Defaults to 1.</summary>
    public Func<int, int> QuerySubject { get; set; } = _ => 1;

    /// <summary>Commands the runner skipped because their meaning is unknown (opcode → count).</summary>
    public Dictionary<byte, int> Skipped { get; } = [];

    /// <summary>Starts a script at its first scene (or the given scene id).</summary>
    public void Start(string name, int? sceneId = null)
    {
        var file = load(name) ?? throw new FileNotFoundException($"Script {name} not found.");
        File = file;
        Jump(sceneId ?? file.Scenes.FirstOrDefault()?.Id ?? 0);
    }

    void Jump(int sceneId)
    {
        _stack.Clear();
        _pendingChoice = null;
        SceneId = sceneId;
        var scene = File?.Scenes.FirstOrDefault(s => s.Id == sceneId);
        if (scene is not null)
            _stack.Push((scene.Commands, 0));
    }

    /// <summary>Runs until the next message, menu or the end.</summary>
    public ScriptEvent Next()
    {
        if (_pendingChoice is not null)
            return _pendingChoice;

        for (int steps = 0; steps < MaxStepsPerEvent; steps++)
        {
            if (_stack.Count == 0)
                return new ScriptEnded();
            var (commands, index) = _stack.Pop();
            if (index >= commands.Count)
                continue; // block finished: back to the enclosing list
            _stack.Push((commands, index + 1));
            var command = commands[index];

            switch (command.Op)
            {
                case SdtFile.OpComment:
                    break;
                case SdtFile.OpMessage:
                    return new ScriptMessage(command.Speaker!.Trim('　', ' '),
                        string.IsNullOrEmpty(command.Voice) ? null : command.Voice,
                        command.Lines!.Select(l => l.Text).ToList());
                case SdtFile.OpGoto:
                {
                    string target = (string)command.Args[0];
                    if (File is null || !target.Equals(File.Name, StringComparison.OrdinalIgnoreCase))
                        File = load(target) ?? throw new FileNotFoundException($"Script {target} not found.");
                    Jump((int)command.Args[1]);
                    break;
                }
                case SdtFile.OpChoice:
                    _choiceCommand = command;
                    _pendingChoice = new ScriptChoice(command.Rows.Select(r => (string)r[0]).ToList());
                    return _pendingChoice;
                case SdtFile.OpSetVar:
                    Variables[(int)command.Args[0]] = (int)command.Args[3];
                    break;
                case SdtFile.OpSwitch:
                {
                    int value = QuerySubject((int)command.Args[0]);
                    int match = command.Rows.FindIndex(r => (int)r[2] == value);
                    if (match >= 0)
                        _stack.Push((command.Blocks[match], 0));
                    break;
                }
                case SdtFile.OpEnd:
                    _stack.Clear();
                    return new ScriptEnded();
                default:
                    Skipped[command.Op] = Skipped.GetValueOrDefault(command.Op) + 1;
                    break;
            }
        }
        throw new InvalidOperationException("Script runs without stopping (endless Goto loop?).");
    }

    /// <summary>Answers the pending menu; the next <see cref="Next"/> continues in the chosen block.</summary>
    public void Choose(int option)
    {
        if (_pendingChoice is null || _choiceCommand is null)
            throw new InvalidOperationException("No menu is waiting for an answer.");
        if (option < 0 || option >= _choiceCommand.Blocks.Count)
            throw new ArgumentOutOfRangeException(nameof(option));
        _stack.Push((_choiceCommand.Blocks[option], 0));
        _pendingChoice = null;
        _choiceCommand = null;
    }
}
