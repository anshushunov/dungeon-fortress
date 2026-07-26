# Dungeon Fortress

Рабочее название проекта.

Dungeon Fortress — игра с непрямым управлением, строительством и развитием
подземного владения, глубокой симуляцией его обитателей, экономикой и тактическими
боями с RPG-элементами.

Проект находится на стадии pre-production. Godot .NET и чистое C#-ядро приняты
как стек первых прототипов ([ADR 0003](docs/decisions/0003-stack-for-prototypes.md),
статус `Accepted`). Контракт первого вертикального прототипа зафиксирован в
[Prototype 1 «Подготовка к набегу»](docs/design/PROTOTYPE_01_PREPARE_FOR_RAID.md).

## Проверка технического spike

Требуются .NET SDK 8.0.423 и Godot 4.7.1 .NET. После настройки
`GODOT4_CONSOLE` или `PATH` одна команда собирает solution, запускает тесты,
проверяет детерминизм, выполняет нагрузочный сценарий и Godot headless smoke:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\verify.ps1
```

Видимый thin host запускается отдельно:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\run-game.ps1
```

Точная настройка, варианты обнаружения Godot и откат описаны в
[`docs/engineering/ENVIRONMENT_SETUP.md`](docs/engineering/ENVIRONMENT_SETUP.md).

## С чего начать

- [Видение игры](docs/product/VISION.md)
- [Контракт Prototype 1](docs/design/PROTOTYPE_01_PREPARE_FOR_RAID.md)
- [Карта документации](docs/README.md)
- [Открытые вопросы](docs/OPEN_QUESTIONS.md)
- [Процесс разработки агентами](docs/engineering/AGENT_WORKFLOW.md)
- [Работа с несколькими агентами](docs/engineering/MULTI_AGENT_WORKFLOW.md)
- [Сравнение технологического стека](docs/engineering/STACK_EVALUATION.md)
- [Технологический радар](docs/engineering/TECH_RADAR.md)
- [Настройка окружения](docs/engineering/ENVIRONMENT_SETUP.md)
- [Журнал архитектурных и продуктовых решений](docs/decisions/README.md)
