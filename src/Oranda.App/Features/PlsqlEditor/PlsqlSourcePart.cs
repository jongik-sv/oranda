using System;
using System.Collections.Generic;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Oranda.App.Features.SqlEditor;
using Oranda.Core.Metadata;

namespace Oranda.App.Features.PlsqlEditor;

/// <summary>패키지·타입 탭의 쪽(PKG1). 명세/본문 쌍이 없는 유형은 늘 <see cref="Spec"/> 한 쪽이다.</summary>
public enum PlsqlSide
{
    Spec,
    Body,
}

/// <summary>
/// PL/SQL 탭의 한 쪽(명세 또는 본문, PKG1). 쪽마다 글·되돌리기·고침 표시(<see cref="Editor"/>)와 서버 기준(마지막 DDL 시각·서버 소스·첫 줄 보정)·오류 목록을 따로 가진다.
/// 패키지·타입이 아닌 탭은 이 쪽 하나만 쓴다.
/// </summary>
public sealed partial class PlsqlSourcePart : ObservableObject
{
    internal PlsqlSourcePart(PlsqlSide side, SqlEditorViewModel editor)
    {
        Side = side;
        Editor = editor;
        Editor.PropertyChanged += OnEditorChanged;
    }

    /// <summary>이 쪽이 명세인지 본문인지. 새 문서를 본문으로 컴파일하면 바뀔 수 있다.</summary>
    [ObservableProperty]
    public partial PlsqlSide Side { get; internal set; }

    /// <summary>이 쪽의 글 편집기(되돌리기 기록·고침 표시가 쪽마다 따로다).</summary>
    public SqlEditorViewModel Editor { get; }

    /// <summary>이 쪽의 서버 객체(<c>PACKAGE</c> 또는 <c>PACKAGE BODY</c> …). 새 문서면 null.</summary>
    public DbObjectRef? Target { get; internal set; }

    /// <summary>열 때(또는 마지막 컴파일 때) 읽은 <c>last_ddl_time</c>.</summary>
    internal DateTime? Baseline { get; set; }

    /// <summary>서버 소스 첫 줄에 앞에 붙인 글만큼의 열 차이(서버 오류 1줄 열 맞추기).</summary>
    internal int Line1Shift { get; set; }

    /// <summary>서버에서 마지막으로 읽은 소스(차이 보기 기준). 없으면 null.</summary>
    internal string? ServerSource { get; set; }

    /// <summary>편집을 시작한 뒤 서버의 이 쪽 소스가 바뀌었다.</summary>
    internal bool ServerChanged { get; set; }

    /// <summary>이 쪽의 오류·경고(서버 순서, <see cref="CompileError.ObjectType"/> 에 이 쪽 유형).</summary>
    internal IReadOnlyList<CompileError> Errors { get; set; } = [];

    /// <summary>글이 채워졌다(서버에서 읽음·저장한 글로 되살림·없는 본문으로 확인). 아니면 이 쪽을 처음 볼 때 서버에서 읽는다.</summary>
    [ObservableProperty]
    public partial bool Loaded { get; internal set; }

    /// <summary>서버에 이 쪽이 없다(본문 없는 패키지). 글이 비어 있으면 「본문 만들기」 안내가 보인다.</summary>
    [ObservableProperty]
    public partial bool Missing { get; internal set; }

    public bool IsDirty => Editor.IsDirty;

    /// <summary>토글 단추 글: 「명세」·「본문」, 고쳤으면 뒤에 「 *」.</summary>
    public string Label => PlsqlEditorViewModel.SideName(Side) + (IsDirty ? " *" : string.Empty);

    partial void OnSideChanged(PlsqlSide value) => OnPropertyChanged(nameof(Label));

    private void OnEditorChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(SqlEditorViewModel.IsDirty))
        {
            OnPropertyChanged(nameof(IsDirty));
            OnPropertyChanged(nameof(Label));
        }
    }
}
