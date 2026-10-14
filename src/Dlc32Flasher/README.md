# Dlc32Flasher — прошивка MKS DLC32 без esptool

Две библиотеки, которые можно взять в любую программу на .NET 8 (они не зависят от PathForge):

| Проект | Что внутри |
|---|---|
| `Dlc32Flasher` (`net8.0`) | Протокол загрузчика ESP32 по COM-порту (`EspRomLoader`) и прошивалка платы (`Dlc32FirmwareFlasher`): запись полного образа флеш-памяти с адреса 0x0 с проверкой MD5, чтение всей флеш-памяти в файл, проверка файла прошивки (`FirmwareImageInfo`). Зависит только от `System.IO.Ports`. |
| `Dlc32Flasher.Wpf` (`net8.0-windows`) | Готовый WPF-элемент `Dlc32FlasherControl` (порт, скорость, кнопки, прогресс, журнал) и окно `Dlc32FlasherWindow`. Сделаны кодом, без XAML. |

Работает с загрузчиком в ПЗУ самой ESP32, по протоколу, который Espressif описывает в разделе «Serial Protocol»
документации esptool. В плату ничего не загружается (вспомогательный код esptool, «stub», не используется), поэтому
запись идёт со скоростью загрузчика: полный образ 8 МБ — 4–5 минут на 460800 бод, чтение 8 МБ — около 10 минут.

## Как подключить к своей программе

Скопируйте папки `src/Dlc32Flasher` и `src/Dlc32Flasher.Wpf` в своё решение и добавьте ссылку на проект:

```xml
<ProjectReference Include="..\Dlc32Flasher.Wpf\Dlc32Flasher.Wpf.csproj" />
```

### Готовое окно (WPF)

```csharp
using Dlc32Flasher;
using Dlc32Flasher.Wpf;

FlasherText.Russian = true;                 // язык текстов: русский или английский
var window = new Dlc32FlasherWindow { Owner = this };
window.Flasher.PortName = "COM5";           // необязательно: порт и скорость по умолчанию
window.Flasher.Baud = 460800;
window.Flasher.BeforePortUse = port =>      // освободить порт, если программа сама держит его
{
    myConnection.Close();
    return true;                            // false — отменить операцию
};
window.ShowDialog();
```

`Dlc32FlasherControl` можно положить в своё окно как обычный элемент. У него есть события `Completed`, `LogLine`,
`BusyChanged`, свойства `IsBusy`, `IsWriting` и методы `WriteFileAsync(path)` / `ReadFileAsync(path)` без диалогов.

### Без интерфейса

```csharp
using Dlc32Flasher;

var flasher = new Dlc32FirmwareFlasher(log: Console.WriteLine);
var progress = new Progress<FlashProgress>(p => Console.WriteLine(p.Text));

FlasherReport report = await flasher.WriteFileAsync("COM5", "dlc32-firmware.bin",
    new Dlc32FlasherOptions { Baud = 460800 }, progress, cancellationToken);
Console.WriteLine(report.Message);          // report.Success, report.FlashChanged, report.Error

report = await flasher.ReadFileAsync("COM5", "dlc32-backup.bin");
```

`Dlc32FlasherOptions`:

- `Baud` — скорость порта во время работы (загрузчик стартует на 115200 и переключается);
- `ResetIntoBootloader` — перевести плату в загрузчик линиями DTR/RTS (схема автосброса MKS DLC32). Выключите для платы,
  которую перевели в загрузчик вручную (GPIO0 на землю + Reset);
- `RestartAfter` — перезапустить плату в прошивку в конце;
- `ConnectAttempts` — сколько раз пробовать сброс и синхронизацию.

Прошивалка записывает только **полный образ флеш-памяти** (загрузчик по 0x1000 и таблица разделов по 0x8000 на месте):
файлы прошивки MKS DLC32 и считанные с платы копии. Отдельные части (загрузчик, приложение) она отклоняет, потому что с
адреса 0x0 они испортили бы флеш-память.

Другие ESP32-платы с обычной схемой автосброса (CH340/CP2102 + два транзистора) тоже подойдут. ESP32-S2/S3/C3 и ESP8266
не поддерживаются: прошивалка проверяет чип и откажется работать с ними.
