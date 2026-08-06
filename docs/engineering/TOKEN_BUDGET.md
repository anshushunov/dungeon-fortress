# Расход токенов: методика замера и базовая линия

Расход токенов — измеряемая величина, а не ощущение. Этот документ описывает,
чем он меряется, и фиксирует базовую линию, относительно которой проверяется
эффект будущих правок. Документ не содержит рекомендаций: он отвечает на
вопрос «сколько», а не «что делать».

## Источник данных

Клиент Claude Code пишет транскрипт каждой сессии в
`~/.claude/projects/<slug>/`, где `<slug>` — путь рабочей копии с заменой
разделителей на дефисы. Для этого репозитория —
`C--gamedev-Dungeon-fortress`.

- `<session-id>.jsonl` — транскрипт основной сессии;
- `<session-id>/subagents/agent-*.jsonl` — транскрипт каждого субагента.

В каждом assistant-сообщении есть объект `message.usage` с четырьмя счётчиками:

| Поле | Что это |
|---|---|
| `input_tokens` | Токены, обработанные без кэша |
| `cache_creation_input_tokens` | Токены, записанные в кэш |
| `cache_read_input_tokens` | Токены, прочитанные из кэша |
| `output_tokens` | Сгенерированные токены |

Полный размер контекста запроса — сумма первых трёх. Отдельного поля с
размером контекста нет, поэтому ниже он везде считается этой суммой.

## Границы метода

- **Это usage, сообщённый API, а не биллинг.** Счётчики отражают, что
  обработала модель; итоговый счёт формируется провайдером и может отличаться.
- **Замер локален.** Учитываются только транскрипты на этой машине. Работа,
  выполненная из другой копии репозитория или другим человеком, в них не
  попадает.
- **Текущая сессия искажает итог.** Сессия, из которой запускается замер,
  дописывает свои же строки во время замера. Поэтому базовая линия ниже
  ограничена завершёнными днями, а срез дат задаётся явно в каждой команде.
- **Пересчёт в деньги приблизителен.** Ниже он приводится в «единицах входного
  токена», а не в валюте, потому что цена зависит от модели, тарифа и
  подписки.

## Как повторить замер

Все команды запускаются из каталога транскриптов:

```bash
cd ~/.claude/projects/C--gamedev-Dungeon-fortress
```

### Сводка за срез дат, отдельно для сессий и субагентов

```bash
for scope in main sub; do
  if [ "$scope" = main ]; then files=$(ls *.jsonl); else files=$(ls */subagents/*.jsonl); fi
  echo "-- $scope --"
  cat $files 2>/dev/null | jq -s --arg from 2026-07-26 --arg to 2026-08-05 '
    [ .[] | select(.message.usage and .timestamp
                   and (.timestamp[0:10] >= $from) and (.timestamp[0:10] <= $to)) ]
    | {calls: length,
       input: (map(.message.usage.input_tokens//0)|add),
       cache_write: (map(.message.usage.cache_creation_input_tokens//0)|add),
       cache_read: (map(.message.usage.cache_read_input_tokens//0)|add),
       output: (map(.message.usage.output_tokens//0)|add),
       avg_ctx: (((map((.message.usage.cache_read_input_tokens//0)
                       + (.message.usage.cache_creation_input_tokens//0)
                       + (.message.usage.input_tokens//0))|add)/length)|floor)}'
done
```

### Расход по дням

```bash
cat *.jsonl */subagents/*.jsonl 2>/dev/null | jq -rs '
 [ .[] | select(.message.usage and .timestamp)
       | {d:(.timestamp[0:10]), u:.message.usage} ]
 | group_by(.d)
 | map({d:.[0].d, calls:length,
        crM:((map(.u.cache_read_input_tokens//0)|add)/1000000|floor),
        cwM:((map(.u.cache_creation_input_tokens//0)|add)/1000000|floor),
        outK:((map(.u.output_tokens//0)|add)/1000|floor)})
 | sort_by(.d) | .[] | "\(.d)  calls=\(.calls)  read=\(.crM)M  write=\(.cwM)M  out=\(.outK)k"'
```

### Самые дорогие субагенты

```bash
for f in */subagents/*.jsonl; do
  jq -s --arg f "$f" --arg from 2026-07-26 --arg to 2026-08-05 '
    [ .[] | select(.message.usage and .timestamp
                   and (.timestamp[0:10]>=$from) and (.timestamp[0:10]<=$to)) ]
    | if length>0
      then "\(((map(.message.usage.cache_read_input_tokens//0)|add)/1000000)|floor)M \(length)calls \(($f|split("/")|.[2]))"
      else empty end' "$f" 2>/dev/null
done | tr -d '"' | sort -rn | head -12
```

### Профиль роста контекста внутри одного прогона

```bash
jq -r 'select(.message.usage)
       | ((.message.usage.cache_read_input_tokens//0)
          + (.message.usage.cache_creation_input_tokens//0))' <путь-к-agent-*.jsonl> \
  | awk 'NR%50==1{printf "%d: %dk\n", NR, $1/1000}'
```

### Распределение вызовов по моделям

```bash
cat *.jsonl */subagents/*.jsonl 2>/dev/null | jq -rs --arg from 2026-07-26 --arg to 2026-08-05 '
 [ .[] | select(.message.model and .timestamp
                and (.timestamp[0:10] >= $from) and (.timestamp[0:10] <= $to))
       | .message.model ]
 | group_by(.) | map({model:.[0], calls:length}) | sort_by(-.calls)
 | .[] | "\(.calls)\t\(.model)"'
```

### Что читают агенты и насколько крупными кусками

```bash
jq -rs --arg from 2026-07-26 --arg to 2026-08-05 '
 [ .[] | select(.timestamp and (.timestamp[0:10]>=$from) and (.timestamp[0:10]<=$to)
                and (.message.content|type=="array"))
       | .message.content[] | select(.type=="tool_use" and .name=="Read")
       | {f:(.input.file_path|split("\\")|last|split("/")|last),
          full:(.input.offset==null and .input.limit==null)} ]
 | group_by(.f) | map({file:.[0].f, reads:length, full:(map(select(.full))|length)})
 | sort_by(-.reads) | .[:14] | .[] | "\(.reads)\t\(.full)\t\(.file)"' \
 *.jsonl */subagents/*.jsonl 2>/dev/null
```

Размеры файлов репозитория меряются по коммиченному состоянию, а не по рабочей
копии — причина в
[правиле о контрольных суммах](../../AGENTS.md):

```bash
git show HEAD:docs/product/ROADMAP.md | wc -c
```

## Базовая линия: 2026-07-26 … 2026-08-05

Срез охватывает всю историю проекта на момент замера, кроме незавершённого
2026-08-06. Все числа получены командами выше.

### Сводка

| | Вызовов | input | cache write | cache read | output | Средний контекст |
|---|---:|---:|---:|---:|---:|---:|
| Основные сессии | 10 076 | 177 012 | 49,1 M | 2 830,6 M | 14,0 M | 285 815 |
| Субагенты | 24 360 | 534 422 | 191,2 M | 4 965,9 M | 12,5 M | 211 727 |
| **Итого** | **34 436** | **0,71 M** | **240,4 M** | **7 796,5 M** | **26,6 M** | — |

Ключевая величина — `cache read`: 7,8 миллиарда токенов. Она равна сумме
размеров контекста по всем вызовам, то есть произведению «число вызовов ×
средний контекст». В сопоставимых единицах входного токена (cache read ≈ 0,1
от цены входного, cache write ≈ 1,25, выходной ≈ 5 — коэффициенты публичного
прайса, они могут меняться) вклад распределяется так:

| Составляющая | Единиц входного токена | Доля |
|---|---:|---:|
| cache read | 780 M | 64 % |
| cache write | 300 M | 25 % |
| output | 133 M | 11 % |
| input | 0,7 M | < 1 % |

### По дням

| Дата | Вызовов | cache read | cache write | output |
|---|---:|---:|---:|---:|
| 2026-07-26 | 341 | 52 M | 2 M | 747 k |
| 2026-07-27 | 950 | 266 M | 4 M | 1 130 k |
| 2026-07-28 | 3 162 | 827 M | 17 M | 3 453 k |
| 2026-07-29 | 2 250 | 506 M | 15 M | 1 761 k |
| 2026-07-30 | 4 603 | 900 M | 28 M | 3 417 k |
| 2026-07-31 | 5 303 | 1 422 M | 42 M | 3 574 k |
| 2026-08-01 | 6 331 | 1 313 M | 44 M | 4 431 k |
| 2026-08-02 | 4 631 | 1 085 M | 44 M | 3 595 k |
| 2026-08-03 | 3 805 | 764 M | 15 M | 2 486 k |
| 2026-08-04 | 1 873 | 459 M | 18 M | 1 229 k |
| 2026-08-05 | 1 187 | 198 M | 7 M | 738 k |

### Субагенты

За срез отработало 127 субагентов, суммарно 4 907 M cache read.

| Порог cache read | Число субагентов |
|---|---:|
| больше 100 M | 11 |
| больше 50 M | 34 |
| больше 20 M | 60 |
| медиана | 18 M |

Двенадцать самых дорогих:

| cache read | Вызовов | Файл |
|---:|---:|---|
| 451 M | 778 | `agent-a05e361bea73b3b8b.jsonl` |
| 249 M | 594 | `agent-aabe9881af9e41d57.jsonl` |
| 186 M | 537 | `agent-adebaf459f13bd979.jsonl` |
| 170 M | 525 | `agent-a65862ce18a0e0193.jsonl` |
| 139 M | 503 | `agent-a4be8caf42d97e9dc.jsonl` |
| 128 M | 451 | `agent-abf41503e1b1aa002.jsonl` |
| 109 M | 359 | `agent-a24cbfcf8c7227ed1.jsonl` |
| 108 M | 515 | `agent-ab993598bc4fd01fc.jsonl` |
| 106 M | 413 | `agent-a7d28ff4d6f833e4b.jsonl` |
| 105 M | 415 | `agent-a6759ddfd17837e04.jsonl` |
| 104 M | 358 | `agent-a67e9cd9ec432bb40.jsonl` |
| 97 M | 366 | `agent-a9c42fcd86ac90f4d.jsonl` |

### Профиль роста контекста

Самый дорогой субагент среза (задача #117) за 778 вызовов:

| Вызов | Контекст |
|---:|---:|
| 1 | 27 k |
| 101 | 317 k |
| 201 | 432 k |
| 301 | 527 k |
| 401 | 641 k |
| 501 | 727 k |
| 601 | 822 k |
| 701 | 875 k |
| 751 | 913 k |

Контекст растёт монотонно и не сбрасывается. Поскольку `cache read` одного
прогона — это сумма контекста по всем его вызовам, при линейном росте она
пропорциональна квадрату числа шагов: 451 M у этого прогона против медианных
18 M.

### Модели

| Вызовов | Модель | Доля |
|---:|---|---:|
| 31 168 | `claude-opus-5` | 90,5 % |
| 3 024 | `claude-sonnet-5` | 8,8 % |
| 224 | `claude-fable-5` | 0,7 % |
| 20 | `<synthetic>` | — |

### Что читают агенты

Столбец «целиком» — чтения без `offset`/`limit`, то есть весь файл в контекст.

| Чтений | Целиком | Файл | Размер по HEAD |
|---:|---:|---|---:|
| 304 | 4 | `Main.cs` | 290 302 B |
| 270 | 19 | `ROADMAP.md` | 427 474 B |
| 143 | 7 | `PrototypeWorld.cs` | 214 508 B |
| 98 | 7 | `PROTOTYPE_01_PREPARE_FOR_RAID.md` | 355 383 B |
| 89 | 18 | `MULTI_AGENT_WORKFLOW.md` | 100 847 B |
| 59 | 16 | `DEBT_LEDGER.md` | 96 237 B |
| 53 | 47 | `README.md` | 6 152 B |
| 51 | 9 | `PROTOTYPE_GRAYBOX.md` | 133 146 B |
| 45 | 25 | `verify.ps1` | — |
| 36 | 11 | `ENVIRONMENT_SETUP.md` | 86 301 B |
| 30 | 12 | `PROTOTYPE_HEADLESS.md` | 40 151 B |
| 28 | 12 | `PrototypeTuning.cs` | — |
| 27 | 22 | `AGENTS.md` | 16 012 B |
| 27 | 14 | `run-game.ps1` | — |

Доля чтений целиком мала у крупных файлов: агенты уже читают их диапазонами.

## Как проверять эффект правки

1. Зафиксировать дату, с которой действует правка.
2. Снять сводку и расход по дням теми же командами за равные по длине срезы до
   и после этой даты.
3. Сравнивать не абсолютный расход, а **расход на единицу работы** — например,
   на смерженный PR за период:

```bash
gh pr list --state merged --limit 200 --json number,mergedAt \
  --jq '[.[]|select(.mergedAt>="2026-07-26" and .mergedAt<="2026-08-05")]|length'
```

Абсолютный расход за период зависит в первую очередь от того, сколько задач
прошло через конвейер, и без нормировки на число задач ничего не показывает.
