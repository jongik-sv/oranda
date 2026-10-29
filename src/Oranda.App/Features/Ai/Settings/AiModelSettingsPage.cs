using System;
using System.Collections.Generic;
using CommunityToolkit.Mvvm.ComponentModel;
using Oranda.Core.Settings;

namespace Oranda.App.Features.Ai.Settings;

/// <summary>
/// 설정 「AI 모델」 쪽 최소판(시안 1): 선택 안 함 / Claude Code + 모델. 값은 설정 파일의 <c>ai</c> 섹션(<see cref="AiSettings"/>)이다.
/// 「선택 안 함」 이면 AI 단추를 비활성으로 두고(아무것도 호출하지 않는다), Claude Code 이면 모델 이름을 고른다.
/// 리터럴 가림 확인란은 개발·시험 접속에만 효력이 있다(운영 접속은 이 값과 관계없이 항상 가린다).
/// 이 쪽이 건드리지 않는 값(실행 파일 경로)은 읽은 그대로 <see cref="ToSettings"/> 에 실려 나간다.
/// </summary>
public sealed partial class AiModelSettingsPage : ObservableObject
{
    public static readonly IReadOnlyList<string> KnownModels = ["opus", "sonnet", "haiku"];

    private readonly AiSettings _source;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ModelEnabled), nameof(IsNone), nameof(IsClaudeCode), nameof(Summary))]
    private AiDriverKind _driver;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Summary))]
    private string _model;

    [ObservableProperty]
    private bool _maskLiterals;

    public AiModelSettingsPage(AiSettings? settings = null)
    {
        _source = settings ?? new AiSettings();
        _driver = _source.Driver;
        _maskLiterals = _source.MaskLiterals;
        _model = string.IsNullOrWhiteSpace(_source.Model) ? KnownModels[0] : _source.Model;
    }

    /// <summary>고를 수 있는 모델 이름(화면 바인딩용 사본).</summary>
    public IReadOnlyList<string> Models { get; } = KnownModels;

    public bool ModelEnabled => Driver == AiDriverKind.ClaudeCode;

    public bool IsNone
    {
        get => Driver == AiDriverKind.None;
        set
        {
            if (value)
            {
                Driver = AiDriverKind.None;
            }
        }
    }

    public bool IsClaudeCode
    {
        get => Driver == AiDriverKind.ClaudeCode;
        set
        {
            if (value)
            {
                Driver = AiDriverKind.ClaudeCode;
            }
        }
    }

    /// <summary>편집기 오른쪽 위에 보이는 한 줄(시안 9 의 「AI: Claude Code · opus」).</summary>
    public string Summary => Driver == AiDriverKind.None ? "AI: 선택 안 함" : $"AI: Claude Code · {Model}";

    /// <summary>설정 파일에 쓸 <c>ai</c> 섹션. 「선택 안 함」 이면 모델을 비운다.</summary>
    public AiSettings ToSettings() => _source with { Driver = Driver, Model = Driver == AiDriverKind.None ? null : Model, MaskLiterals = MaskLiterals };
}
