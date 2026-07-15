using System.Text.RegularExpressions;

namespace AndroidLogViewer.Models;

public sealed partial class LogEntry
{
    /// <summary>
    /// 创建一个正则表达式，用于解析日志条目的时间、进程ID、线程ID、日志级别、标签和消息。
    /// </summary>
    private static readonly Regex ThreadTimeRegex = CreateThreadTimeRegex();
    /// <summary>
    /// 命令行输出一行就创建一个Entry 
    /// </summary>
    /// <param name="rawLine"></param>
    public LogEntry(string rawLine)
    {
        RawLine = rawLine;
        Parse(rawLine);
    }

    public string RawLine { get; }

    public string Time { get; private set; } = string.Empty;

    public string ProcessId { get; private set; } = string.Empty;

    public string ThreadId { get; private set; } = string.Empty;

    public string Level { get; private set; } = string.Empty;

    public string Tag { get; private set; } = string.Empty;

    public string Message { get; private set; } = string.Empty;

    /// <summary>
    /// 按照日志等级来过滤日志条目，返回是否匹配
    /// </summary>
    /// <param name="textFilter"></param>
    /// <param name="tagFilter"></param>
    /// <param name="minimumLevel"></param>
    /// <returns></returns>
    public bool Matches(string textFilter, string tagFilter, string minimumLevel)
    {
        if (!PassesMinimumLevel(minimumLevel))
        {
            return false;
        }

        string[] tagFilters = SplitTagFilters(tagFilter);
        if (tagFilters.Length > 0
            && !tagFilters.Any(tag => Tag.Contains(tag, StringComparison.OrdinalIgnoreCase)))
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(textFilter))
        {
            return true;
        }

        return RawLine.Contains(textFilter.Trim(), StringComparison.OrdinalIgnoreCase);
    }

    public bool PassesMinimumLevel(string minimumLevel)
    {
        return PassesLevel(Level, minimumLevel);
    }

    private void Parse(string rawLine)
    {
        /// <summary>
        /// 匹配日志条目的正则表达式模式：
        Match match = ThreadTimeRegex.Match(rawLine);
        if (!match.Success)
        {
            Message = rawLine;
            return;
        }

        Time = match.Groups["time"].Value;
        ProcessId = match.Groups["pid"].Value;
        ThreadId = match.Groups["tid"].Value;
        Level = match.Groups["level"].Value;
        Tag = match.Groups["tag"].Value.Trim();
        Message = match.Groups["message"].Value;
    }

    private static bool PassesLevel(string level, string minimumLevel)
    {
        if (string.IsNullOrWhiteSpace(minimumLevel) || minimumLevel == "All")
        {
            return true;
        }

        return LevelRank(level) >= LevelRank(minimumLevel);
    }

    private static int LevelRank(string level)
    {
        // 将日志级别字符串映射到整数等级，以便进行比较
        return level.Trim().ToUpperInvariant() switch
        {
            "V" or "VERBOSE" => 0,
            "D" or "DEBUG" => 1,
            "I" or "INFO" => 2,
            "W" or "WARN" => 3,
            "E" or "ERROR" => 4,
            "F" or "FATAL" => 5,
            _ => 0
        };
    }

    /// <summary>
    /// 解析tag过滤器字符串
    /// 
    /// 分割符号：，; 空格、制表符、换行符、竖线    
    /// 分割后删除空白项目  删除前后空格
    /// 过滤掉空白的tag
    /// 去重
    /// 
    /// </summary>
    /// <param name="tagFilter"></param>
    /// <returns></returns>
    private static string[] SplitTagFilters(string tagFilter)
    {
        return tagFilter
            .Split([',', ';', ' ', '\t', '\r', '\n', '|'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(tag => !string.IsNullOrWhiteSpace(tag))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    [GeneratedRegex(@"^(?<time>\d{2}-\d{2}\s+\d{2}:\d{2}:\d{2}\.\d{3})\s+(?<pid>\d+)\s+(?<tid>\d+)\s+(?<level>[VDIWEF])\s+(?<tag>[^:]+):\s?(?<message>.*)$")]
    private static partial Regex CreateThreadTimeRegex();
}
