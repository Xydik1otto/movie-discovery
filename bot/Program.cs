using MovieDiscovery.Bot;
using MovieDiscovery.Bot.Api;
using Telegram.Bot;
using Telegram.Bot.Polling;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;

try
{
    Console.OutputEncoding = System.Text.Encoding.UTF8;
}
catch
{
    // не критично (наприклад, перенаправлений вивід)
}

var config = BotConfig.FromEnvironment();
if (string.IsNullOrWhiteSpace(config.BotApiKey))
    Log.Error("BOT_API_KEY порожній — X-Bot-Key не надсилається; персональні операції (списки/оцінки) fail-closed: помилка bot_key_not_configured замість запису в спільний гостьовий профіль.");
using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    cts.Cancel();
};

// Без токена бот НЕ падає (контейнер restart: unless-stopped): тиха нескінченна пауза,
// лог-нагадування раз на 30 с. Щойно власник заповнить TG_BOT_TOKEN у .env і перезапустить
// контейнер — бот підніметься.
if (string.IsNullOrWhiteSpace(config.Token))
{
    Log.Error("TG_BOT_TOKEN порожній — заповніть .env");
    while (!cts.IsCancellationRequested)
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(30), cts.Token);
            Log.Info("Очікування TG_BOT_TOKEN…");
        }
        catch (OperationCanceledException)
        {
            break;
        }
    }

    Log.Info("Вихід.");
    return;
}

var api = new ApiClient(config.ApiUrl, config.BotApiKey);
var state = new ChatState();
var botClient = new TelegramBotClient(config.Token);
var handler = new BotUpdateHandler(botClient, api, state, Log.Info);

// Чекаємо валідний токен / зв'язок з Telegram — ретраї кожні 30 с, не падаємо.
User? me = null;
while (me is null && !cts.IsCancellationRequested)
{
    try
    {
        me = await botClient.GetMe(cts.Token);
    }
    catch (OperationCanceledException)
    {
        break;
    }
    catch (Exception ex)
    {
        Log.Error($"Telegram недоступний або токен невалідний ({ex.Message}). Повторна спроба за 30 с.");
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(30), cts.Token);
        }
        catch (OperationCanceledException)
        {
            break;
        }
    }
}

if (me is null)
{
    Log.Info("Вихід.");
    return;
}

Log.Info($"Бот @{me.Username} запущено · API_URL={config.ApiUrl} · long polling");

botClient.StartReceiving(
    handler.HandleUpdateAsync,
    (_, ex, _, _) =>
    {
        Log.Error($"Polling: {ex.Message}");
        return Task.CompletedTask;
    },
    new ReceiverOptions
    {
        DropPendingUpdates = true,
        AllowedUpdates = [UpdateType.Message, UpdateType.CallbackQuery],
    },
    cts.Token);

try
{
    await Task.Delay(Timeout.Infinite, cts.Token);
}
catch (OperationCanceledException)
{
    // Ctrl+C / SIGTERM — штатне завершення
}

Log.Info("Бот зупинено.");
