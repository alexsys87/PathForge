# Сторонние компоненты

| Компонент | Версия | Лицензия | Где используется |
|---|---|---|---|
| [Clipper2](https://github.com/AngusJohnson/Clipper2) (Angus Johnson) | 1.5.4 | Boost Software License 1.0 | `PathForge.Core`: эквидистанты и булевы операции с многоугольниками |
| [CommunityToolkit.Mvvm](https://github.com/CommunityToolkit/dotnet) (.NET Foundation) | 8.4.0 | MIT | `PathForge.App`: MVVM |
| [System.IO.Ports](https://github.com/dotnet/runtime) (.NET Foundation) | 8.0.0 | MIT | `PathForge.App`: связь со станком по COM-порту |
| [xUnit.net](https://github.com/xunit/xunit) | 2.9.2 | Apache 2.0 | Только тесты |
| Microsoft.NET.Test.Sdk | 17.11.1 | MIT | Только тесты |

Все пакеты подключаются из NuGet; их исходный код в репозиторий не копировался.
При распространении программы приложите тексты лицензий Clipper2 и CommunityToolkit.Mvvm
(они есть в соответствующих NuGet-пакетах); System.IO.Ports — часть .NET (MIT).
