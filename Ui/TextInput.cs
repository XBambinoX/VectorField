using System.Text;

namespace VectorField.Ui;

public sealed class TextInput
{
    readonly StringBuilder text = new();

    public string Text => text.ToString();
    public int Length => text.Length;
    public int Caret { get; private set; }

    public double IdleTime { get; set; }

    public void Set(string value)
    {
        text.Clear().Append(value);
        Caret = value.Length;
        IdleTime = 0;
    }

    public void Insert(string value)
    {
        foreach (char c in value)
            if (!char.IsControl(c))
                text.Insert(Caret++, c);
        IdleTime = 0;
    }

    public void Backspace(bool word)
    {
        int start = word ? WordStart(Caret) : Math.Max(Caret - 1, 0);
        text.Remove(start, Caret - start);
        Caret = start;
        IdleTime = 0;
    }

    public void Delete(bool word)
    {
        int end = word ? WordEnd(Caret) : Math.Min(Caret + 1, text.Length);
        text.Remove(Caret, end - Caret);
        IdleTime = 0;
    }

    public void Left(bool word) => Move(word ? WordStart(Caret) : Caret - 1);
    public void Right(bool word) => Move(word ? WordEnd(Caret) : Caret + 1);
    public void Home() => Move(0);
    public void End() => Move(text.Length);

    public void Move(int position)
    {
        Caret = Math.Clamp(position, 0, text.Length);
        IdleTime = 0;
    }

    int WordStart(int i)
    {
        while (i > 0 && !char.IsLetterOrDigit(text[i - 1])) i--;
        while (i > 0 && char.IsLetterOrDigit(text[i - 1])) i--;
        return i;
    }

    int WordEnd(int i)
    {
        while (i < text.Length && !char.IsLetterOrDigit(text[i])) i++;
        while (i < text.Length && char.IsLetterOrDigit(text[i])) i++;
        return i;
    }
}
