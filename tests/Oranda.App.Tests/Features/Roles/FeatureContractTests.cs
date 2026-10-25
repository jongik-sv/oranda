using System.Reflection;
using System.Text.RegularExpressions;
using Oranda.App.Features.Localization;
using Oranda.App.Shell;
using Oranda.App.Shell.Composition;
using Oranda.App.Shell.Features;
using Oranda.Core.Features;
using Oranda.Core.Shell;

namespace Oranda.App.Tests.Features.Roles;

/// <summary>
/// TSK-07-01 계약 시험(공유): 게이트가 앱 조립에 한 번 등록되고 features 없음 = 모두 켬, 카탈로그 꼬리표가 실제 앱 코드를 가리킴,
/// WP-07 예약 문구가 ko/en·literals 에 모두 있음. 틀의 존재·탑재만 단정한다(해석·거르기는 기능 Task 시험).
/// </summary>
public sealed partial class FeatureContractTests
{
    [GeneratedRegex("[\\uAC00-\\uD7A3]")]
    private static partial Regex Hangul();

    private static ServiceContainer Compose() =>
        AppComposition.Create(new AppCompositionOptions
        {
            DataDirectory = Path.Combine(Path.GetTempPath(), "oranda-roles-" + Guid.NewGuid().ToString("N")),
            DevBuild = true,
        });

    [Fact]
    public void Gate_is_registered_once_as_a_singleton_and_resolves_without_scanning_deferred_assemblies()
    {
        using var services = Compose();
        var gate = services.GetRequiredService<IFeatureGate>();

        Assert.IsType<FeatureGate>(gate);
        Assert.Same(gate, services.GetRequiredService<IFeatureGate>());
        Assert.False(services.DeferredScanned); // 게이트를 물어도 Oracle·Language 어셈블리를 열지 않는다
    }

    [Fact]
    public void Without_a_features_section_every_registered_item_is_enabled()
    {
        using var services = Compose();
        var gate = services.GetRequiredService<IFeatureGate>();
        var contributions = services.GetRequiredService<ShellContributions>();

        Assert.All(FeatureIds.All, id => Assert.True(gate.IsEnabled(id), id));
        Assert.All(contributions.Commands, c => Assert.True(gate.IsCommandEnabled(c.Definition.Id, c.HandlerType), c.Definition.Id));
        Assert.All(contributions.Panels, p => Assert.True(gate.IsPanelEnabled(p.Id, p.ViewModelType), p.Id));
        Assert.All(contributions.StatusBarItems, s => Assert.True(gate.IsTypeEnabled(s.ItemType), s.Id));
        Assert.False(gate.ReadOnlyRole);
        Assert.Empty(gate.PendingRestart);
    }

    [Fact]
    public void Namespace_tags_point_at_real_feature_folders()
    {
        var namespaces = typeof(AppComposition).Assembly.GetTypes().Select(t => t.Namespace).OfType<string>().ToHashSet(StringComparer.Ordinal);
        foreach (var tag in FeatureCatalog.NamespaceTags)
        {
            Assert.True(
                namespaces.Any(ns => ns == tag.Namespace || ns.StartsWith(tag.Namespace + ".", StringComparison.Ordinal)),
                "앱에 없는 네임스페이스: " + tag.Namespace);
        }
    }

    [Fact]
    public void Id_and_type_overrides_point_at_real_commands_and_types()
    {
        var commands = ShellContributions.Scan(typeof(AppComposition).Assembly).Commands.Select(c => c.Definition.Id).ToHashSet(StringComparer.Ordinal);
        foreach (var id in FeatureCatalog.IdOverrides.Keys)
        {
            Assert.True(commands.Contains(id), "등록되지 않은 명령 id: " + id);
        }

        var platform = Assembly.Load("Oranda.Platform");
        foreach (var name in FeatureCatalog.TypeOverrides.Keys)
        {
            Assert.NotNull(platform.GetType(name) ?? typeof(AppComposition).Assembly.GetType(name));
        }
    }

    [Fact]
    public void Roles_command_id_is_reserved_with_titles_in_both_languages()
    {
        Assert.Equal("oranda.tools.roles", FeatureCommandIds.Roles);
        Assert.Equal("Role·기능 고르기…", StringCatalog.Get("ko", "Cmd." + FeatureCommandIds.Roles));
        Assert.DoesNotMatch(Hangul(), StringCatalog.Get("en", "Cmd." + FeatureCommandIds.Roles)!);
    }

    [Fact]
    public void Every_reserved_text_exists_in_korean_and_english_and_in_the_literal_table()
    {
        var keys = FeatureCatalog.Features.Select(f => f.NameKey)
            .Concat(FeatureCatalog.Roles.SelectMany(r => new[] { r.NameKey, r.DescriptionKey }))
            .Concat(Enum.GetValues<FeatureGroup>().Select(FeatureCatalog.GroupNameKey))
            .Concat(typeof(FeatureTexts).GetFields(BindingFlags.Public | BindingFlags.Static)
                .Where(f => f.IsLiteral && f.Name != nameof(FeatureTexts.ReadOnlyRoleDeniedMessage) && !f.Name.EndsWith("Prefix", StringComparison.Ordinal))
                .Select(f => (string)f.GetRawConstantValue()!))
            .ToList();
        Assert.Equal(25 + 12 + 5 + 48, keys.Count); // FeatureTexts 48 = WP-07 28 − 기능 탭 모드·제목 4 + 주 업무·덧붙임·고급·팩 링크 17(TSK-09-08) + 새 설치 카드·접속 추가 7(TSK-09-09)

        foreach (var key in keys)
        {
            var ko = StringCatalog.Get("ko", key);
            var en = StringCatalog.Get("en", key);
            Assert.True(ko is not null, "ko 없음: " + key);
            Assert.True(en is not null, "en 없음: " + key);
            Assert.Matches(Hangul(), ko);
            Assert.DoesNotMatch(Hangul(), en);
            Assert.True(LiteralTranslator.Table.TryGetValue(ko!, out var literal), "literals-en 없음: " + ko);
            Assert.Equal(en, literal);
        }

        // Core 정책 관문이 쓰는 한국어 거부 문구는 예약 문구와 같다.
        Assert.Equal(FeatureTexts.ReadOnlyRoleDeniedMessage, StringCatalog.Get("ko", FeatureTexts.ReadOnlyRoleDenied));
    }
}
