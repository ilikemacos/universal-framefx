namespace UniversalFrameFX.PresetAi;

public static class GptTokenizer
{
    static readonly char[] Chars;
    static readonly Dictionary<char, int> Stoi;
    static readonly Dictionary<int, char> Itos;
    public const int VocabSize = 98;
    public const int MaxContext = 448;
    public const int NewlineId = 96;
    public const int TabId = 95;
    public const int UnkId = 97;

    static GptTokenizer()
    {
        var list = new List<char>();
        for (int i = 32; i < 127; i++) list.Add((char)i);
        list.Add('\t');
        list.Add('\n');
        Chars = list.ToArray();
        Stoi = new Dictionary<char, int>();
        Itos = new Dictionary<int, char>();
        for (int i = 0; i < Chars.Length; i++)
        {
            int id = i + 1;
            Stoi[Chars[i]] = id;
            Itos[id] = Chars[i];
        }
        Stoi['?'] = UnkId;
    }

    public static IReadOnlyList<int> Encode(string s) =>
        s.Select(c => Stoi.GetValueOrDefault(c, UnkId)).ToList();

    public static string Decode(IEnumerable<int> ids) =>
        string.Concat(ids.Select(i => Itos.GetValueOrDefault(i, '?')));

    public static string PreparePrompt(string userText)
    {
        if (string.IsNullOrEmpty(userText)) return "";
        return userText.Length > 300 ? userText[^300..] : userText;
    }
}
