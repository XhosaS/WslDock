using System.Windows.Automation;
using System.Windows.Shapes;

namespace WslDock;

internal sealed class WslStatusIndicator : Border
{
    private readonly Ellipse dot = new() { StrokeThickness = 1 };
    internal bool? IsRunning { get; private set; }

    internal WslStatusIndicator()
    {
        Width = Height = 5;
        Child = dot;
        HorizontalAlignment = HorizontalAlignment.Center;
        SetRunning(null);
    }

    internal void SetRunning(bool? running)
    {
        IsRunning = running;
        dot.Fill = running == true ? Ui.Brush("#0F7B0F") : Brushes.Transparent;
        dot.Stroke = Ui.Brush(running == true ? "#0F7B0F" : "#777777");
        var label = running switch
        {
            true => "WSL 正在运行",
            false => "WSL 未启动",
            null => "WSL 状态暂不可用"
        };
        ToolTip = label;
        AutomationProperties.SetName(this, label);
    }
}
