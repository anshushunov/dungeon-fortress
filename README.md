# Dungeon Fortress

Игра с непрямым управлением: игрок вырубает из скалы подземное владение и
населяет его существами, которые живут своей жизнью. Он не отдаёт приказов, а
проектирует пространство, задаёт правила и иногда вмешивается рукой. Регулярно
приходят те, кто хочет забрать накопленное.

Стек: Godot 4.7.1 .NET как хост, симуляция и представление на чистом C# без
ссылок на движок ([ADR 0003](docs/decisions/0003-stack-for-prototypes.md),
[ADR 0011](docs/decisions/0011-presentation-layer-without-engine.md)).

## Запуск

Нужны .NET SDK 8.0.423 и Godot 4.7.1 .NET. Настройка —
[`docs/engineering/ENVIRONMENT_SETUP.md`](docs/engineering/ENVIRONMENT_SETUP.md).

```powershell
# Тесты без движка
dotnet test tests\DungeonFortress.Simulation.Tests -c Release
dotnet test tests\DungeonFortress.Presentation.Tests -c Release

# Игра
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\run-game.ps1

# Полная проверка перед PR: сборка, тесты, детерминизм, Godot smoke, золотые кадры HUD
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\verify.ps1
```

## С чего начать

- [Питч: главная идея и core gameplay](docs/product/PITCH.md)
- [Roadmap: что сделано, что дальше](docs/product/ROADMAP.md)
- [Карта документации](docs/README.md)
- [Правила работы в репозитории](AGENTS.md)
