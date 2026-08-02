using System.Text;

namespace PrismOne.Db.Core;

/// <summary>
/// 한글(두벌식 IME 입력)을 원래 눌렀던 QWERTY 키로 역변환한다.
/// 비밀번호 입력에서 IME 를 끄는 것을 잊었을 때 "암호" → "dkagh" 로 복원하는 용도.
/// </summary>
public static class HangulQwerty
{
    private static readonly string[] Choseong =
    [
        "r","R","s","e","E","f","a","q","Q","t","T","d","w","W","c","z","x","v","g"
    ];

    private static readonly string[] Jungseong =
    [
        "k","o","i","O","j","p","u","P","h","hk","ho","hl","y","n","nj","np","nl","b","m","ml","l"
    ];

    private static readonly string[] Jongseong =
    [
        "","r","R","rt","s","sw","sg","e","f","fr","fa","fq","ft","fx","fv","fg",
        "a","q","qt","t","T","d","w","c","z","x","v","g"
    ];

    // 호환 자모 0x3131(ㄱ) ~ 0x3163(ㅣ)
    private static readonly string[] CompatJamo =
    [
        "r","R","rt","s","sw","sg","e","E","f","fr","fa","fq","ft","fx","fv","fg",
        "a","q","Q","qt","t","T","d","w","W","c","z","x","v","g",
        "k","o","i","O","j","p","u","P","h","hk","ho","hl","y","n","nj","np","nl","b","m","ml","l"
    ];

    /// <summary>한글 음절/자모를 QWERTY 키열로 바꾼다. 그 외 문자는 그대로 통과.</summary>
    public static string Convert(string text)
    {
        var sb = new StringBuilder(text.Length * 2);
        foreach (var c in text)
        {
            if (c is >= '가' and <= '힣')
            {
                var index = c - 0xAC00;
                sb.Append(Choseong[index / (21 * 28)]);
                sb.Append(Jungseong[index / 28 % 21]);
                sb.Append(Jongseong[index % 28]);
            }
            else if (c is >= 'ㄱ' and <= 'ㅣ')
            {
                sb.Append(CompatJamo[c - 0x3131]);
            }
            else
            {
                sb.Append(c);
            }
        }
        return sb.ToString();
    }
}
