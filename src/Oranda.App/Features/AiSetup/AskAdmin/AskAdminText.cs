using Oranda.App.Shell;

namespace Oranda.App.Features.AiSetup.AskAdmin;

/// <summary>「담당자에게 묻기」 단계의 한/영 글. 글 공급자가 없으면 앱 글 입구의 언어를 본다.</summary>
internal sealed class AskAdminText(IShellText? text)
{
    public bool IsEnglish => text is not null ? text.Language == "en" : Contract.AiSetupText.CurrentIsEnglish;

    public string T(string ko, string en) => IsEnglish ? en : ko;

    public string Title => T("담당자에게 물어보세요", "Ask your administrator");

    public string Intro => T(
        "아래 글을 복사해서 메신저로 담당자에게 보내세요. 답을 받으면 이 도우미를 다시 열면 됩니다.",
        "Copy the message below and send it to your administrator. When you get an answer, open this helper again.");

    public string CopyButton => T("복사", "Copy");

    public string Copied => T("복사했어요. 메신저에 붙여 넣어 보내세요.", "Copied. Paste it into your messenger and send it.");

    public string CopyFailed => T(
        "복사하지 못했어요. 글을 직접 골라 복사하세요.",
        "Could not copy. Select the message and copy it yourself.");
}
