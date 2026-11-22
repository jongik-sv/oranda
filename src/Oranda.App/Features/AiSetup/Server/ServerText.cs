using Oranda.App.Shell;

namespace Oranda.App.Features.AiSetup.Server;

/// <summary>「회사 AI 서버」 단계의 한/영 글. 글 공급자가 없으면 앱 글 입구의 언어를 본다.</summary>
internal sealed class ServerText(IShellText? text)
{
    public bool IsEnglish => text is not null ? text.Language == "en" : Contract.AiSetupText.CurrentIsEnglish;

    public string T(string ko, string en) => IsEnglish ? en : ko;

    public string DefaultName => T("회사 AI", "Company AI");

    public string BadAddress => T(
        "주소가 올바르지 않아요. https:// 로 시작하는 주소를 그대로 붙여 넣으세요.",
        "The address is not valid. Paste the full address that starts with https://.");

    public string NoModel => T(
        "쓸 모델을 정하지 못했어요. 「모델 새로고침」을 누르거나 담당자에게 모델 이름을 물어보세요.",
        "No model was chosen. Press \"Refresh models\" or ask your administrator for the model name.");

    public string KeyNeeded => T("이 서버는 열쇠(API 키)가 필요해요. 담당자에게 받은 키를 넣으세요.", "This server needs a key (API key). Enter the key from your administrator.");

    public string ActivateFailed => T(
        "서버는 저장했지만 사용할 AI 로 고르지 못했어요: ",
        "The server was saved, but it could not be selected as the AI to use: ");

    public string ModelsFailed => T(
        "서버에서 모델 목록을 받지 못했어요. 주소와 열쇠를 확인하고 다시 눌러 보세요.",
        "Could not get the model list from the server. Check the address and key, then try again.");

    public string NoModels => T(
        "서버가 알려 준 모델이 없어요. 담당자에게 모델 이름을 물어보세요.",
        "The server did not list any models. Ask your administrator for the model name.");

    public string NoProbe => T("이 판에서는 서버에 물어볼 수 없어요.", "This version cannot ask the server.");
}
