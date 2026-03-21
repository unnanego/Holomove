namespace Holomove;

public class ProgressBar
{
    private readonly int _barWidth;

    public ProgressBar(int barWidth = 30)
    {
        _barWidth = barWidth;
    }

    public void Update(int current, int total, string? label = null)
    {
        if (total <= 0) return;

        var fraction = (double)current / total;
        var filled = (int)(fraction * _barWidth);
        var empty = _barWidth - filled;
        var percent = fraction * 100;

        var bar = new string('\u2588', filled) + new string('\u2591', empty);
        var counter = $"({current}/{total})";
        var text = label != null ? $" {label}" : "";

        var line = $"\r  [{bar}] {percent,5:F1}% {counter}{text}";

        // Pad to terminal width to clear previous longer lines
        var width = Math.Max(Console.WindowWidth, 80);
        Console.Write(line.PadRight(width - 1));
    }

    public void Complete(string? message = null)
    {
        var width = Math.Max(Console.WindowWidth, 80);
        if (message != null)
        {
            Console.Write($"\r  {message}".PadRight(width - 1));
        }
        Console.WriteLine();
    }
}
