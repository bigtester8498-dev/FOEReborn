using System.Collections.Generic;
using System.Globalization;

namespace FOEReLauncher
{
    /// <summary>
    /// Every text of the window in one table: key -> (Russian, English). The language follows the
    /// system UI language unless the player picked one (settings.json "language").
    /// </summary>
    public static class Texts
    {
        private static readonly Dictionary<string, string[]> Table = new Dictionary<string, string[]>
        {
            ["title"] = new[] { "FOE Reborn", "FOE Reborn" },
            ["window"] = new[] { "FOE Reborn — лаунчер", "FOE Reborn Launcher" },
            ["tab.game"] = new[] { "Игра", "Game" },
            ["installed"] = new[] { "Установлено: {0}", "Installed: {0}" },
            ["available"] = new[] { "Доступно: {0}", "Available: {0}" },
            ["checking"] = new[] { "проверка...", "checking..." },
            ["none"] = new[] { "не установлено", "not installed" },
            ["dash"] = new[] { "—", "—" },
            ["btn.checking"] = new[] { "Проверка...", "Checking..." },
            ["btn.install"] = new[] { "Установить", "Install" },
            ["btn.update"] = new[] { "Обновить", "Update" },
            ["btn.play"] = new[] { "Играть", "Play" },
            ["btn.retry"] = new[] { "Повторить проверку", "Check again" },
            ["btn.running"] = new[] { "Игра запущена", "Game is running" },
            ["btn.check"] = new[] { "Проверить обновления", "Check for updates" },
            ["btn.notes"] = new[] { "Что нового", "What's new" },
            ["btn.verify"] = new[] { "Проверить файлы", "Verify files" },
            ["btn.folder"] = new[] { "Открыть папку игры", "Open game folder" },
            ["btn.gameupdate"] = new[] { "Обновить игру до {0}", "Update the game to {0}" },
            ["btn.launcher"] = new[] { "Обновить лаунчер до {0}", "Update the launcher to {0}" },
            ["btn.close"] = new[] { "Закрыть", "Close" },
            ["channel"] = new[] { "Канал:", "Channel:" },
            ["channel.prod"] = new[] { "Релиз", "Release" },
            ["channel.test"] = new[] { "Тестирование", "Testing" },
            ["lang.switch"] = new[] { "EN", "RU" },
            ["st.checking"] = new[] { "Проверяю последнюю версию игры...", "Checking the latest game version..." },
            ["st.checkfail"] = new[] { "Не удалось проверить обновления: {0}", "Could not check for updates: {0}" },
            ["st.nochannel"] = new[] { "В этом канале пока нет опубликованной версии.", "No version is published in this channel yet." },
            ["st.notinstalled"] = new[] { "Игра не установлена. Полная загрузка: {0}.", "The game is not installed. Full download: {0}." },
            ["st.differs"] = new[] { "В канале опубликована версия {0}. Можно играть в установленную или обновить.", "Version {0} is published in this channel. Play the installed one or update." },
            ["st.differs.running"] = new[] { "Доступна другая версия. Обновить можно после закрытия игры.", "Another version is available. It can be installed after the game is closed." },
            ["st.latest"] = new[] { "Установлена последняя версия.", "The latest version is installed." },
            ["st.running"] = new[] { "Игра запущена.", "The game is running." },
            ["st.scan"] = new[] { "Сверяю файлы: {0} из {1}", "Checking files: {0} of {1}" },
            ["st.download"] = new[] { "Загрузка: {0} из {1} · {2}/с", "Downloading: {0} of {1} · {2}/s" },
            ["st.extract"] = new[] { "Распаковка: {0} из {1} файлов", "Unpacking: {0} of {1} files" },
            ["st.apply"] = new[] { "Устанавливаю файлы...", "Installing files..." },
            ["st.done"] = new[] { "Готово: {0}. Файлов обновлено: {1}, удалено: {2}, загружено {3} за {4} с.", "Done: {0}. Files updated: {1}, removed: {2}, downloaded {3} in {4} s." },
            ["st.uptodate"] = new[] { "Все файлы в порядке, загружать нечего.", "All files are fine, nothing to download." },
            ["st.backup"] = new[] { " Ваши файлы с теми же именами сохранены как .bak: {0}.", " Your files with the same names were kept as .bak: {0}." },
            ["st.failed"] = new[] { "Ошибка: {0}. Установленная версия не изменена; повторите, загрузка продолжится.", "Error: {0}. The installed version is unchanged; try again and the download resumes." },
            ["st.playfail"] = new[] { "Не удалось запустить игру: {0}", "Could not start the game: {0}" },
            ["st.launcher"] = new[] { "Обновляю лаунчер до {0}...", "Updating the launcher to {0}..." },
            ["st.launcher.restart"] = new[] { "Лаунчер обновлён, перезапуск...", "Launcher updated, restarting..." },
            ["st.launcher.fail"] = new[] { "Не удалось обновить лаунчер: {0}", "Could not update the launcher: {0}" },
            ["st.nofolder"] = new[] { "Папки игры ещё нет.", "The game folder does not exist yet." },
            ["whatsnew"] = new[] { "Что нового в {0}", "What's new in {0}" },
            ["notes.none"] = new[] { "Список изменений пока не опубликован.", "No change list is published yet." },
            ["notes.tech"] = new[] { "Технические изменения.", "Technical changes." },
            ["notes.new"] = new[] { "Ещё не установлено (обновлений: {0})", "Not installed yet ({0} updates)" },
            ["notes.new1"] = new[] { "Ещё не установлено (одно обновление)", "Not installed yet (1 update)" },
            ["notes.installed"] = new[] { "Установлена: {0}", "Installed: {0}" },
            ["notes.more"] = new[] { "+ ещё обновлений с вашей версии: {0}, см. «Что нового»", "+ {0} more updates since your version, see What's new" },
            ["notes.more1"] = new[] { "+ ещё одно обновление с вашей версии, см. «Что нового»", "+ 1 more update since your version, see What's new" },
            ["launcher.version"] = new[] { "версия лаунчера: {0}", "launcher version: {0}" },
            ["st.blocked.running"] = new[] { "Игра запущена: установка, обновление и проверка файлов недоступны, пока она не закрыта.", "The game is running: install, update and file checks wait until it is closed." },

            // Tab "Mods": the game's mod list. State words and reasons are the game's own (Content/reborn/text).
            ["tab.mods"] = new[] { "Моды", "Mods" },
            ["mods.col.name"] = new[] { "Мод", "Mod" },
            ["mods.col.version"] = new[] { "Версия", "Version" },
            ["mods.col.source"] = new[] { "Откуда", "From" },
            ["mods.col.state"] = new[] { "Состояние", "State" },
            ["mods.src.game"] = new[] { "с игрой", "with the game" },
            ["mods.src.catalogue"] = new[] { "из каталога", "catalogue" },
            ["mods.src.hand"] = new[] { "добавлен вручную", "added by hand" },
            ["mods.codetag"] = new[] { "[код]", "[code]" },
            ["mods.st.on"] = new[] { "вкл.", "on" },
            ["mods.st.noconsent"] = new[] { "код не разрешён", "code not allowed" },
            ["mods.st.off"] = new[] { "выкл.", "off" },
            ["mods.st.missing"] = new[] { "нет зависимости", "missing dep." },
            ["mods.st.needs"] = new[] { "нужен мод", "needs a mod" },
            ["mods.st.version"] = new[] { "не та версия", "wrong version" },
            ["mods.st.incompatible"] = new[] { "несовместим", "incompatible" },
            ["mods.st.cycle"] = new[] { "цикл порядка", "order cycle" },
            ["mods.st.broken"] = new[] { "сломан", "broken" },
            ["mods.st.duplicate"] = new[] { "дубликат", "duplicate" },
            ["mods.btn.up"] = new[] { "Выше", "Move up" },
            ["mods.btn.down"] = new[] { "Ниже", "Move down" },
            ["mods.btn.refresh"] = new[] { "Обновить список", "Refresh" },
            ["mods.btn.folder"] = new[] { "Папка модов", "Mods folder" },
            ["mods.hint"] = new[] { "Галочка включает мод. «Выше» и «Ниже» меняют порядок загрузки: поздние моды побеждают. Изменения сохраняются сразу и видны в игре при следующем запуске.", "The box enables a mod. Move up and Move down change the load order: later mods win. Changes are saved at once and the game uses them at its next start." },
            ["mods.readonly"] = new[] { "Игра запущена: список модов только для просмотра. Измените его в меню «Моды» игры или закройте игру.", "The game is running: the mod list is read-only. Change it in the game's Mods menu or close the game." },
            ["mods.none"] = new[] { "Моды не найдены. Положите папку мода (с package.json) в папку Mods или установите мод из каталога.", "No mods found. Put a mod folder (with package.json) into the Mods folder or install one from the catalogue." },
            ["mods.nogame"] = new[] { "Игра ещё не установлена.", "The game is not installed yet." },
            ["mods.d.by"] = new[] { "автор: {0}", "by {0}" },
            ["mods.d.state"] = new[] { "Состояние: {0}", "State: {0}" },
            ["mods.d.enabled"] = new[] { "Включён, загружается {0}-м", "Enabled, loads as number {0}" },
            ["mods.d.disabled"] = new[] { "Выключен", "Disabled" },
            ["mods.d.requires"] = new[] { "Требует: {0}", "Requires: {0}" },
            ["mods.d.notinstalled"] = new[] { "(не установлен)", "(not installed)" },
            ["mods.d.loadafter"] = new[] { "Загружается после: {0}", "Loads after: {0}" },
            ["mods.d.loadbefore"] = new[] { "Загружается до: {0}", "Loads before: {0}" },
            ["mods.d.incompatible"] = new[] { "Несовместим с: {0}", "Incompatible with: {0}" },
            ["mods.d.code"] = new[] { "Содержит программный код ({0})", "Contains program code ({0})" },
            ["mods.d.allowed"] = new[] { ", разрешён", ", allowed" },
            ["mods.d.notallowed"] = new[] { ", ещё не разрешён", ", not allowed yet" },
            ["mods.d.folder"] = new[] { "Папка: {0}", "Folder: {0}" },
            ["mods.d.issues"] = new[] { "Замечания к package.json: {0}", "package.json notes: {0}" },
            ["mods.confirm.title"] = new[] { "Подтвердите", "Please confirm" },
            ["mods.confirm.on"] = new[] { "«{0}» не работает без этих модов, они тоже будут включены: {1}", "\"{0}\" needs these mods; they are enabled too: {1}" },
            ["mods.confirm.off"] = new[] { "Этим модам нужен «{0}», они тоже будут выключены: {1}", "These mods need \"{0}\"; they are disabled too: {1}" },
            ["mods.alsoon"] = new[] { "Включены и они, без них {0} не работает: {1}", "Also enabled, {0} needs them: {1}" },
            ["mods.alsooff"] = new[] { "Выключены и они, им нужен {0}: {1}", "Also disabled, they need {0}: {1}" },
            ["mods.saved"] = new[] { "Сохранено. Игра применит список при следующем запуске.", "Saved. The game uses the list at its next start." },
            ["mods.c.title"] = new[] { "Этот мод содержит программный код", "This mod contains program code" },
            ["mods.c.text"] = new[] { "«{0}» запускает внутри игры свою программу ({1}). У модов с кодом тот же доступ к компьютеру и файлам, что и у игры. FOE Reborn не может проверить, что они делают. Включайте мод, только если доверяете его автору. При смене версии мода вопрос будет задан снова.", "\"{0}\" runs its own program inside the game ({1}). Code mods have the same access to your computer and files as the game. FOE Reborn cannot check what they do. Enable it only if you trust its author. You are asked again when the mod's version changes." },
            ["mods.c.allow"] = new[] { "Разрешить и включить", "Allow and enable" },
            ["btn.ok"] = new[] { "Продолжить", "Continue" },
            ["btn.cancel"] = new[] { "Отмена", "Cancel" },

            // Tab "Catalogue".
            ["tab.catalogue"] = new[] { "Каталог", "Catalogue" },
            ["cat.search"] = new[] { "Поиск:", "Search:" },
            ["cat.col.kind"] = new[] { "Тип", "Kind" },
            ["cat.col.size"] = new[] { "Размер", "Size" },
            ["cat.kind.data"] = new[] { "данные", "data" },
            ["cat.kind.code"] = new[] { "код", "code" },
            ["cat.st.notinstalled"] = new[] { "не установлен", "not installed" },
            ["cat.st.installed"] = new[] { "установлен", "installed" },
            ["cat.st.update"] = new[] { "есть обновление: {0}", "update available: {0}" },
            ["cat.st.withgame"] = new[] { "с игрой", "with the game" },
            ["cat.st.byhand"] = new[] { "добавлен вручную", "added by hand" },
            ["cat.st.needsgame"] = new[] { "нужна игра {0}", "needs game {0}" },
            ["cat.btn.install"] = new[] { "Установить", "Install" },
            ["cat.btn.update"] = new[] { "Обновить", "Update" },
            ["cat.btn.updateall"] = new[] { "Обновить все ({0})", "Update all ({0})" },
            ["cat.btn.remove"] = new[] { "Удалить", "Remove" },
            ["cat.btn.replace"] = new[] { "Заменить версией из каталога", "Replace with the catalogue version" },
            ["cat.btn.refresh"] = new[] { "Обновить каталог", "Refresh" },
            ["cat.loading"] = new[] { "Загружаю каталог...", "Loading the catalogue..." },
            ["cat.empty"] = new[] { "Каталог модов пока не опубликован.", "No mod catalogue is published yet." },
            ["cat.failed"] = new[] { "Не удалось загрузить каталог: {0}", "Could not load the catalogue: {0}" },
            ["cat.count"] = new[] { "Модов в каталоге: {0}", "Mods in the catalogue: {0}" },
            ["cat.d.by"] = new[] { "автор: {0}", "by {0}" },
            ["cat.d.requires"] = new[] { "Требует: {0}", "Requires: {0}" },
            ["cat.d.game"] = new[] { "Для игры: {0}", "For game: {0}" },
            ["cat.d.homepage"] = new[] { "Страница: {0}", "Page: {0}" },
            ["cat.d.installed"] = new[] { "Установлена версия {0}", "Installed version {0}" },
            ["cat.confirm.reconsent"] = new[] { "«{0}» обновлён до {1} и включён, но код новой версии ещё не разрешён: игра не запустит его, пока вы не разрешите. Разрешайте, только если доверяете автору ({2}).", "\"{0}\" is updated to {1} and enabled, but the code of the new version is not allowed yet: the game will not run it until you allow it. Allow it only if you trust its author ({2})." },
            ["cat.btn.allow"] = new[] { "Разрешить", "Allow" },
            ["cat.confirm.code"] = new[] { "«{0}» содержит программный код. Он будет работать с правами вашей учётной записи, как и игра. FOE Reborn не может проверить, что он делает. Устанавливайте его, только если доверяете автору ({1}).", "\"{0}\" contains program code. It runs with your account's rights, as the game does. FOE Reborn cannot check what it does. Install it only if you trust its author ({1})." },
            ["cat.confirm.deps"] = new[] { "Для «{0}» будут установлены и эти моды из каталога: {1}", "\"{0}\" needs these catalogue mods; they are installed too: {1}" },
            ["cat.confirm.remove"] = new[] { "Удалить «{0}»?", "Remove \"{0}\"?" },
            ["cat.confirm.remove.deps"] = new[] { "Удалить «{0}»? Эти моды требуют его и будут выключены: {1}", "Remove \"{0}\"? These mods require it and will be disabled: {1}" },
            ["cat.confirm.replace"] = new[] { "Папка «{0}» добавлена вручную. Она будет перенесена в {1}, а на её место встанет версия {2} из каталога.", "The folder of \"{0}\" was added by hand. It moves to {1} and the catalogue version {2} takes its place." },
            ["cat.st.downloading"] = new[] { "Загружаю {0}: {1}%", "Downloading {0}: {1}%" },
            ["cat.st.installed.done"] = new[] { "Установлено: {0}. Включите на вкладке «Моды».", "Installed: {0}. Enable it on the Mods tab." },
            ["cat.st.updated.done"] = new[] { "Обновлено: {0}.", "Updated: {0}." },
            ["cat.st.removed.done"] = new[] { "Удалён: {0}.", "Removed: {0}." },
            ["cat.st.removed.deps"] = new[] { "Удалён: {0}. Выключены: {1}.", "Removed: {0}. Disabled: {1}." },
            ["cat.st.backup"] = new[] { "Старая папка сохранена: {0}", "The old folder is kept in {0}" },
            ["cat.st.failed"] = new[] { "Не установлено: {0}", "Not installed: {0}" },
            ["mods.err.running"] = new[] { "игра запущена, закройте её", "the game is running, close it first" },
            ["mods.err.reqmissing"] = new[] { "«{0}» требует «{1}», а его нет ни в игре, ни в каталоге", "\"{0}\" requires '{1}', which is neither installed nor in the catalogue" },
            ["mods.err.withgame"] = new[] { "«{0}» поставляется с игрой", "\"{0}\" comes with the game" },
            ["mods.err.byhand"] = new[] { "папка «{0}» добавлена вручную: сначала замените её версией из каталога", "the folder of \"{0}\" was added by hand: replace it with the catalogue version first" },
            ["mods.err.notbyhand"] = new[] { "«{0}» не добавлен вручную", "\"{0}\" was not added by hand" },
            ["mods.err.notfound"] = new[] { "мод «{0}» не найден", "mod \"{0}\" not found" },
            ["mods.err.notmine"] = new[] { "«{0}» установлен не из каталога", "\"{0}\" was not installed from the catalogue" },
            ["mods.err.archive"] = new[] { "архив «{0}» повреждён", "the archive of \"{0}\" is damaged" },
            ["mods.err.unsafe"] = new[] { "в архиве «{0}» недопустимое имя файла: {1}", "the archive of \"{0}\" has an unsafe file name: {1}" },
            ["mods.err.toobig"] = new[] { "архив «{0}» распаковывается в слишком большой объём ({1})", "the archive of \"{0}\" unpacks to too much data ({1})" },
            ["mods.err.nomanifest"] = new[] { "в архиве «{0}» нет package.json в корне", "the archive of \"{0}\" has no package.json at its root" },
            ["mods.err.wrongmod"] = new[] { "в архиве не тот мод: ждали {0} {1}, внутри {2} {3}", "the archive holds another mod: expected {0} {1}, found {2} {3}" },
            ["mods.err.hash"] = new[] { "загруженный файл не совпал с каталогом (размер или SHA-256)", "the downloaded file does not match the catalogue (size or SHA-256)" }
        };

        /// <summary>"ru" or "en".</summary>
        public static string Language { get; set; } = SystemLanguage();

        public static string SystemLanguage() =>
            CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "ru" ? "ru" : "en";

        public static string T(string key, params object[] args)
        {
            if (!Table.TryGetValue(key, out var pair))
            {
                return key;
            }

            var text = Language == "ru" ? pair[0] : pair[1];
            return args.Length == 0 ? text : string.Format(CultureInfo.CurrentCulture, text, args);
        }

        public static IEnumerable<string> Keys => Table.Keys;

        public static bool HasBoth(string key) =>
            Table.TryGetValue(key, out var pair) && pair.Length == 2 && pair[0].Length > 0 && pair[1].Length > 0;
    }
}
