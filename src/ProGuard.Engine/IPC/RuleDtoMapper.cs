using ProGuard.Core.Models;
using ProGuard.Shared.Contracts;

namespace ProGuard.Engine.IPC;

/// <summary>Converts between the stored <see cref="ProcessRule"/> and the <see cref="ProcessRuleDto"/> sent over IPC.</summary>
public static class RuleDtoMapper
{
    public static ProcessRuleDto ToDto(ProcessRule rule) => DtoMapper.Map<ProcessRule, ProcessRuleDto>(rule);

    /// <summary>Copies every field of <paramref name="dto"/> onto <paramref name="rule"/>, including its Id.</summary>
    public static void CopyTo(ProcessRuleDto dto, ProcessRule rule) => DtoMapper.CopyTo(dto, rule);
}
