# Документация проекта

Git-репозиторий — долговременная память проекта. Решение не считается
зафиксированным, если оно осталось только в чате.

## Разделы

- `product/` — видение, аудитория, цели и границы продукта.
- `design/` — живой game design document, механики и игровые формулы.
- `engineering/` — архитектура, инструменты, процесс и эксплуатация.
- `decisions/` — ADR: принятые решения, их контекст и последствия.
- `art/` — происхождение ассетов и пайплайн их получения.
- `OPEN_QUESTIONS.md` — вопросы, по которым решение ещё не принято.

Главная идея игры и core gameplay — [`product/PITCH.md`](product/PITCH.md).
Питч принят владельцем и задаёт критерий, по которому кандидаты сравниваются
на каждом decision gate.

Текущее направление, завершённые игровые эксперименты и точки, в которых
владелец выбирает следующий прототип, находятся в
[`product/ROADMAP.md`](product/ROADMAP.md).

Игры-референсы и то, за чем именно идти к каждой из них, —
[`product/REFERENCES.md`](product/REFERENCES.md). Способы получения анимации и
перечень надёжно генерируемых ассетов —
[`art/ANIMATION_PIPELINE.md`](art/ANIMATION_PIPELINE.md).

Контракт текущего вертикального прототипа —
`design/PROTOTYPE_01_PREPARE_FOR_RAID.md`. Это источник истины для реализации
Prototype 1; `design/GDD.md` описывает игру шире и на уровне намерения. История
получения раздела 13.4 контракта — снятые и переписанные инварианты, разбор
разменов боевых задач #101 и #129, измерения вне матрицы Issue #12 — вынесена в
[`design/PROTOTYPE_01_CONTRACT_HISTORY.md`](design/PROTOTYPE_01_CONTRACT_HISTORY.md),
чтобы implementation-агент не платил за неё при каждом чтении контракта.

Спека текущего блока по интерфейсу — `design/UI_CONTROL_PASS.md`: панель на
иконках, подсказки и выделение мышью. Чем тело говорит, на чьей оно стороне, —
`design/SIDE_INDICATOR.md`: контур по силуэту, окрашенный по отношению к игроку,
и таблица, в которую дописываются нейтралы и фракции.

Текущие технологические кандидаты описаны в
`engineering/STACK_EVALUATION.md`, а порядок поиска и принятия новых технологий —
в `engineering/TECH_RADAR.md`.

Воспроизводимая настройка проверяемого Godot/.NET spike описана в
`engineering/ENVIRONMENT_SETUP.md`. Там же правило о производных файлах Godot:
`.godot/`, `*.import` и `*.uid` не отслеживаются, поэтому после импорта проекта
`git status` остаётся пустым.

Запуск и наблюдение headless-экономики Prototype 1 описаны в
`engineering/PROTOTYPE_HEADLESS.md`.

Запуск, управление и воспроизводимый screenshot визуального Phase A graybox
описаны в `engineering/PROTOTYPE_GRAYBOX.md`.

Сборка повторяемых PNG, manifest для PR, безопасная диагностика GitHub
authentication и точный token usage handoff описаны в
`engineering/EVIDENCE_WORKFLOW.md`.

Чем меряется расход токенов проекта и какова его базовая линия —
[`engineering/TOKEN_BUDGET.md`](engineering/TOKEN_BUDGET.md): разбивка по
дням, сессиям и субагентам, и правило сравнивать расход на смерженный PR, а не
абсолютный. Сам замер — один вызов `scripts/token-budget-report.ps1`
(стратификация writer/review на агента, отказ по незакрытому срезу).

Правила работы Codex, Claude Code и других агентов находятся в
`engineering/MULTI_AGENT_WORKFLOW.md`.

Правила входа агента, которые `take-task.ps1` печатает в начале каждого брифа, —
`engineering/AGENT_ENTRY.md`: изоляция, партиция, запрет следов и формат отчёта
телом PR.

Находки независимого review, у которых нет наблюдаемого последствия ни для
игрока, ни для запускаемой проверки, лежат в
[`engineering/DEBT_LEDGER.md`](engineering/DEBT_LEDGER.md). Там же записано, при
каком условии запись повышается до Issue и кто перечитывает реестр. Правила
темпа разработки, которым он подчинён, —
[`engineering/DEVELOPMENT_PACE.md`](engineering/DEVELOPMENT_PACE.md).

Решения владельца на decision gate — [`product/GATE_DECISIONS.md`](product/GATE_DECISIONS.md):
дата, решение, исход и последствия. Критерий отказа в
[`product/ROADMAP.md`](product/ROADMAP.md) опирается на этот журнал.

## Что где фиксировать

| Содержание | Место |
|---|---|
| Стабильное видение и продуктовые принципы | `product/VISION.md` |
| Главная идея и core gameplay | `product/PITCH.md` |
| Текущее направление и очередь работ | `product/ROADMAP.md` |
| Решение владельца на decision gate | `product/GATE_DECISIONS.md` |
| Текущее устройство механики | `design/` |
| Варианты, выбор и последствия важного решения | `decisions/` |
| Незакрытый вопрос | `OPEN_QUESTIONS.md` |
| Процессное правило: темп, review, параллелизм | `engineering/DEVELOPMENT_PACE.md` |
| Находка review без наблюдаемого последствия | `engineering/DEBT_LEDGER.md` |
| Замер расхода токенов и базовая линия | `engineering/TOKEN_BUDGET.md` |
| Конкретная работа с критериями готовности | GitHub Issue |
| Группа задач и порядок выполнения | GitHub Project |
| Обсуждение и проверка изменения | Pull Request |

Документы описывают актуальную систему, а ADR объясняют, почему она стала такой.
История чата может помогать работе, но не является источником истины.
