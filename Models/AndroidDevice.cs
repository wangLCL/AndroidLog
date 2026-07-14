namespace AndroidLogViewer.Models;

/// <summary>
/// 数据类，不可以被继承
/// </summary>
/// <param name="Serial">设备序列号</param>
/// <param name="State">设备状态</param>
/// <param name="DisplayName">设备显示名称</param>
public sealed record AndroidDevice(string Serial, string State, string DisplayName)
{
    public override string ToString()
    {
        return DisplayName;
    }
}
