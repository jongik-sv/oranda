using System;
using System.Globalization;
using Oranda.App.Features.AiSetup.Contract;
using Oranda.Core.Ai.Setup;

namespace Oranda.App.Features.AiSetup.Test;

/// <summary>연결 시험 단계 글(한/영). 원인 한 줄은 계약 <see cref="AiSetupErrorText.For"/>, 여기는 머리·안내·단추만.</summary>
internal static class AiSetupTestText
{
    public static string Intro(bool english) =>
        AiSetupText.T(english, "AI 에게 아주 짧은 질문을 하나 보냅니다. DB 는 건드리지 않습니다.", "We send the AI one very short question. Your databases are not touched.");

    public static string Running(bool english) => AiSetupText.T(english, "AI 에게 묻는 중…", "Asking the AI…");

    /// <summary>성공 머리. 걸린 시간을 소수 한 자리 초로.</summary>
    public static string Succeeded(TimeSpan elapsed, bool english)
    {
        var seconds = elapsed.TotalSeconds.ToString("0.0", CultureInfo.InvariantCulture);
        return AiSetupText.T(english, $"AI 가 답했습니다 · {seconds}초", $"The AI answered · {seconds} s");
    }

    public static string SucceededDetail(bool english) =>
        AiSetupText.T(english, "연결이 잘 됩니다. 「다음」을 누르세요.", "The connection works. Press Next.");

    public static string Failed(bool english) => AiSetupText.T(english, "연결 시험이 실패했습니다", "The connection check failed");

    /// <summary>실패 종류별로 직원이 해 볼 일 한 줄.</summary>
    public static string WhatToDo(AiSetupErrorKind kind, AiSetupPath? path, bool english) => kind switch
    {
        AiSetupErrorKind.CliNotFound => AiSetupText.T(english, "「이전」으로 돌아가 설치를 다시 해 보세요.", "Go Back and run the install again."),
        AiSetupErrorKind.NotLoggedIn => AiSetupText.T(english, "「이전」으로 돌아가 로그인을 다시 해 보세요.", "Go Back and sign in again."),
        AiSetupErrorKind.Network => AiSetupText.T(
            english,
            "회사 VPN 이나 인터넷에 연결되어 있는지 확인한 뒤 「다시 시험」을 누르세요.",
            "Check that you are on the company VPN or the internet, then press Try again."),
        AiSetupErrorKind.Timeout => AiSetupText.T(english, "잠시 뒤 「다시 시험」을 누르세요.", "Wait a moment, then press Try again."),
        AiSetupErrorKind.ServerRejected when path == AiSetupPath.CompanyServer => AiSetupText.T(
            english,
            "「이전」으로 돌아가 열쇠와 모델을 확인하거나 담당자에게 물어보세요.",
            "Go Back and check the key and model, or ask your admin."),
        AiSetupErrorKind.ServerRejected => AiSetupText.T(
            english,
            "회사 계정으로 Claude Code 를 쓸 수 있는지 담당자에게 물어보세요.",
            "Ask your admin whether your company account may use Claude Code."),
        _ => AiSetupText.T(english, "아래 진단 글을 복사해 담당자에게 보내 주세요.", "Copy the details below and send them to your admin."),
    };

    public static string StillFailing(bool english) =>
        AiSetupText.T(english, "그래도 안 되면 아래 진단 글을 담당자에게 보내세요.", "If it still fails, send the details below to your admin.");

    /// <summary>갈래 B 인데 회사 AI 서버 단계에서 만든 연결이 없다.</summary>
    public static string NoProfile(bool english) =>
        AiSetupText.T(english, "회사 AI 서버 설정이 없습니다. 「이전」으로 돌아가 먼저 끝내세요.", "There is no company AI server set up. Go Back and finish that step first.");

    /// <summary>이 창에서 시험 기능을 쓸 수 없다(조립이 덜 됨).</summary>
    public static string Unavailable(bool english) =>
        AiSetupText.T(english, "이 창에서는 연결 시험을 할 수 없습니다.", "The connection check is not available here.");

    public static string Retry(bool english) => AiSetupText.T(english, "다시 시험", "Try again");

    public static string Copy(bool english) => AiSetupText.T(english, "진단 글 복사", "Copy details");

    public static string Copied(bool english) => AiSetupText.T(english, "복사했습니다", "Copied");
}
