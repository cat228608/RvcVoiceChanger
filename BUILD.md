# Сборка

## Что нужно

- Windows 10/11 x64
- .NET SDK 8.0 (https://dotnet.microsoft.com/download/dotnet/8.0)

## Команда

```powershell
cd RvcVoiceChanger
dotnet restore
dotnet publish src/RvcVoiceChanger/RvcVoiceChanger.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true
```

Готовый файл:

```
src\RvcVoiceChanger\bin\Release\net8.0-windows\win-x64\publish\RvcVoiceChanger.exe
```

Это один исполняемый файл (~70–90 МБ): внутри .NET-рантайм, NAudio и скрипт `vc_worker.py`. Копируется куда угодно, установка не нужна.

## Отладка

```powershell
dotnet run --project src/RvcVoiceChanger/RvcVoiceChanger.csproj
```

## Структура проекта

```
Core/         пути, логи, настройки
Net/          HTTP с прокси, загрузчик с докачкой
Runtime/      установщик Python/PyTorch/бэкенда, мост к python
Audio/        WASAPI-устройства, VB-CABLE, аудиодвижок
Models/       библиотека моделей, импорт и валидация
HuggingFace/  парсер репозиториев
Views/        окна и вкладки
Assets/python/ vc_worker.py + requirements-extra.txt (вшиты в exe)
```

## Замечания

- Первый запуск требует интернета: скачивается 3–6 ГБ (в основном PyTorch CUDA).
- Для CUDA нужен свежий драйвер NVIDIA; без GPU автоматически ставится CPU-версия (работает, но с большей задержкой — ставьте чанк от 192).
- Антивирус может ругаться на неподписанный single-file exe — подпишите своим сертификатом при распространении.
