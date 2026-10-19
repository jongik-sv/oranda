// Claude Code 상태줄: 모델·브랜치 / ctx·5h·7d 사용량.  stdin = 상태줄 JSON, stdout = 두 줄.
import { execFileSync } from 'node:child_process'

const EIGHTHS = ['', '▏', '▎', '▍', '▌', '▋', '▊', '▉', '█']

const bar = (percent, width = 5) => {
  const p = Math.max(0, Math.min(100, Number(percent) || 0))
  let eighths = Math.ceil((p / 100) * width * 8)
  if (p > 0 && eighths === 0) eighths = 1
  let out = ''
  for (let i = 0; i < width; i++) {
    const cell = Math.max(0, Math.min(8, eighths - i * 8))
    out += cell === 0 ? '░' : EIGHTHS[cell]
  }
  return out
}

// resets_at: 에포크 초(숫자) 또는 ISO 문자열.
// 오늘 안에 풀리면 "6pm", 아니면 "10/23 10pm, 4d 8h" (날짜 시각, 남은 일·시간)
const resetText = (value, now) => {
  if (value === undefined || value === null || value === '') return ''
  const ms = typeof value === 'number' ? (value < 1e12 ? value * 1000 : value) : Date.parse(value)
  if (Number.isNaN(ms)) return ''
  const at = new Date(ms)
  const h24 = at.getHours()
  const h12 = h24 % 12 === 0 ? 12 : h24 % 12
  const mm = at.getMinutes()
  const clock = `${h12}${mm === 0 ? '' : `:${String(mm).padStart(2, '0')}`}${h24 < 12 ? 'am' : 'pm'}`
  if (at.toDateString() === new Date(now).toDateString()) return clock
  const left = Math.max(0, ms - now)
  const days = Math.floor(left / 86_400_000)
  const hours = Math.floor((left % 86_400_000) / 3_600_000)
  const minutes = Math.floor((left % 3_600_000) / 60_000)
  const remain = days > 0 ? `${days}d ${hours}h` : hours > 0 ? `${hours}h ${minutes}m` : `${minutes}m`
  return `${at.getMonth() + 1}/${at.getDate()} ${clock}, ${remain}`
}

const git = (cwd, args) => {
  try {
    return execFileSync('git', args, { cwd, timeout: 1500, stdio: ['ignore', 'pipe', 'ignore'] })
      .toString()
      .trim()
  } catch {
    return ''
  }
}

let raw = ''
for await (const chunk of process.stdin) raw += chunk
let d = {}
try {
  d = JSON.parse(raw)
} catch {
  // 빈 입력이면 빈 상태줄
}

const cwd = d.workspace?.current_dir ?? d.cwd ?? process.cwd()
const model = d.model?.display_name ?? d.model?.id ?? ''
const branch = git(cwd, ['branch', '--show-current']) || '-'
const dirty = git(cwd, ['status', '--porcelain']).length > 0
const now = Date.now()

const pct = (v) => Math.round(Number(v) || 0)
const parts = []
const ctx = d.context_window?.used_percentage
if (ctx !== undefined && ctx !== null) parts.push(`${bar(ctx)} ${pct(ctx)}%`)
for (const key of ['five_hour', 'seven_day']) {
  const l = d.rate_limits?.[key]
  if (!l) continue
  const reset = resetText(l.resets_at, now)
  parts.push(`${bar(l.used_percentage)} ${pct(l.used_percentage)}%${reset ? `(${reset})` : ''}`)
}

const head = `${model ? `[${model}] | ` : ''}${branch}${dirty ? '*' : ''}`
process.stdout.write(`${head}\n${parts.length ? `ctx: ${parts.join(' | ')}\n` : ''}`)
