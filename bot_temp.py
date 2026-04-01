#!/usr/bin/env python3
"""
WinFlow Telegram Bot — работает на VPS 24/7.
Админ-панель: управление клиентами, генерация токенов, мониторинг.
Умные уведомления об откате с динамическими интервалами.
Динамическое добавление/удаление админов через бота.
"""

import os
import json
import logging
import requests
from datetime import datetime, timezone, timedelta
from telegram import (
    Update, InlineKeyboardButton, InlineKeyboardMarkup,
    ReplyKeyboardMarkup, KeyboardButton
)
from telegram.ext import (
    Application, CommandHandler, CallbackQueryHandler,
    MessageHandler, filters, ContextTypes
)

# === НАСТРОЙКИ ===
BOT_TOKEN = "8394906281:AAEhRCN2hJxV7uPfZw-UnISXcAcHEHonago"
BASE_ADMIN_IDS = [942720632, 852340170, 5207644382]  # Базові адміни (хардкод)
ADMIN_IDS = list(BASE_ADMIN_IDS)  # Робочий список (база + динамічні)
ADMIN_USERNAMES = ["rudoy_chell"]  # Резерв по username
API_URL = "http://127.0.0.1:5000/api"
TOKEN_LIFETIME_HOURS = 72
ADMINS_FILE = "/opt/winflow-bot/admins.json"

logging.basicConfig(
    format="%(asctime)s [%(levelname)s] %(message)s",
    level=logging.INFO
)
log = logging.getLogger("winflow-bot")


# =============================================
# ДИНАМІЧНЕ УПРАВЛІННЯ АДМІНАМИ
# =============================================

# Стан очікування вводу chat_id
_awaiting_admin_add: dict[int, bool] = {}


def load_dynamic_admins():
    """Завантажити додаткових адмінів з файлу."""
    global ADMIN_IDS
    try:
        if os.path.exists(ADMINS_FILE):
            with open(ADMINS_FILE, "r") as f:
                data = json.load(f)
                dynamic_ids = data.get("admin_ids", [])
                for aid in dynamic_ids:
                    if aid not in ADMIN_IDS:
                        ADMIN_IDS.append(aid)
                log.info(f"Loaded {len(dynamic_ids)} dynamic admins from {ADMINS_FILE}")
    except Exception as e:
        log.error(f"Failed to load admins: {e}")


def save_dynamic_admins():
    """Зберегти динамічних адмінів у файл."""
    try:
        # Зберігаємо тільки ті ID, яких немає в BASE_ADMIN_IDS
        dynamic_ids = [aid for aid in ADMIN_IDS if aid not in BASE_ADMIN_IDS]
        with open(ADMINS_FILE, "w") as f:
            json.dump({"admin_ids": dynamic_ids, "updated": datetime.utcnow().isoformat()}, f, indent=2)
        log.info(f"Saved {len(dynamic_ids)} dynamic admins to {ADMINS_FILE}")
    except Exception as e:
        log.error(f"Failed to save admins: {e}")


def add_admin(chat_id: int) -> bool:
    """Додати адміна. Повертає True якщо додано."""
    if chat_id in ADMIN_IDS:
        return False
    ADMIN_IDS.append(chat_id)
    save_dynamic_admins()
    return True


def remove_admin(chat_id: int) -> bool:
    """Видалити адміна. Не можна видалити базових."""
    if chat_id in BASE_ADMIN_IDS:
        return False
    if chat_id in ADMIN_IDS:
        ADMIN_IDS.remove(chat_id)
        save_dynamic_admins()
        return True
    return False


# =============================================
# REPLY KEYBOARD
# =============================================

MAIN_KEYBOARD = ReplyKeyboardMarkup(
    [
        [KeyboardButton("🔑 Новый токен"), KeyboardButton("📋 Все клиенты")],
        [KeyboardButton("💰 Оплаченные"), KeyboardButton("⏳ Неоплаченные"), KeyboardButton("🧪 Тестовые")],
        [KeyboardButton("👤 Добавить админа"), KeyboardButton("📊 Статистика"), KeyboardButton("❓ Помощь")],
    ],
    resize_keyboard=True,
    is_persistent=True
)


# =============================================
# ГЕНЕРАЦИЯ ТОКЕНОВ (через VPS API)
# =============================================

def generate_token() -> str:
    """Генерирует короткий токен XX-XX-XX-XX-XX-XX (6 пар цифр) через VPS API."""
    try:
        r = requests.post(
            f"{API_URL}/tokens/generate",
            json={"lifetime_hours": TOKEN_LIFETIME_HOURS},
            timeout=5
        )
        data = r.json()
        if data.get("ok"):
            return data["token"]
        return "ERROR"
    except Exception as e:
        log.error(f"Token generation error: {e}")
        return "ERROR"


# =============================================
# VPS API
# =============================================

def api_get_clients(status_filter: str = None) -> list:
    try:
        params = {"status": status_filter} if status_filter else {}
        r = requests.get(f"{API_URL}/clients", params=params, timeout=5)
        return r.json().get("clients", [])
    except Exception as e:
        log.error(f"API error (clients): {e}")
        return []


def api_update_status(client_id: str, new_status: str) -> bool:
    try:
        r = requests.post(
            f"{API_URL}/clients/{client_id}/status",
            json={"status": new_status}, timeout=5
        )
        return r.json().get("ok", False)
    except Exception as e:
        log.error(f"API error (update_status): {e}")
        return False


def api_delete_client(client_id: str) -> bool:
    try:
        r = requests.delete(f"{API_URL}/clients/{client_id}", timeout=5)
        return r.json().get("ok", False)
    except Exception as e:
        log.error(f"API error (delete): {e}")
        return False


def api_update_notification(client_id: str):
    try:
        requests.post(f"{API_URL}/clients/{client_id}/notification", timeout=5)
    except Exception:
        pass


def api_set_pending_action(client_id: str, action: str):
    """Встановити pending_action для клієнта (rollback_cleanup / payment_cleanup)."""
    try:
        requests.post(
            f"{API_URL}/clients/{client_id}/pending_action",
            json={"action": action}, timeout=5
        )
    except Exception:
        pass


# =============================================
# ФОРМАТИРОВАНИЕ
# =============================================

def format_client(c: dict) -> str:
    status_emoji = {"paid": "💰", "unpaid": "⏳", "testing": "🧪"}
    emoji = status_emoji.get(c.get("status", ""), "❓")
    status = c.get("status", "unknown").upper()

    lines = [
        f"{emoji} <b>{c.get('pc_name', '?')}</b> [{status}]",
        f"   🆔 <code>{c.get('client_id', '?')}</code>",
        f"   🔧 HWID: {c.get('hwid', '?')[:12]}...",
    ]

    if c.get("ip_address"):
        lines.append(f"   🌐 IP: {c['ip_address']}")
    if c.get("last_seen"):
        lines.append(f"   🕐 Последний раз: {c['last_seen']}")
    if c.get("install_date"):
        lines.append(f"   📅 Установка: {c['install_date']}")
    if c.get("payment_date"):
        lines.append(f"   💳 Оплата: {c['payment_date']}")
    if c.get("rollback_status"):
        lines.append(f"   ⏪ Откат: {c['rollback_status']}")
    if c.get("token_used"):
        lines.append(f"   🔑 Токен: {c['token_used']}")
    if c.get("rollback_deadline"):
        lines.append(f"   ⏰ Дедлайн: {c['rollback_deadline']}")

    return "\n".join(lines)


def is_admin(user_id: int, username: str = None) -> bool:
    if user_id in ADMIN_IDS:
        return True
    if username and username.lower() in [u.lower() for u in ADMIN_USERNAMES]:
        # Автоматично додаємо ID в список щоб наступні перевірки були швидшими
        ADMIN_IDS.append(user_id)
        log.info(f"Admin added by username: @{username} (ID: {user_id})")
        return True
    return False


# =============================================
# ОТПРАВКА СПИСКА КЛИЕНТОВ
# =============================================

async def _send_clients_list(message, status_filter: str):
    clients = api_get_clients(status_filter)

    if not clients:
        label_map = {"paid": "оплаченных", "unpaid": "неоплаченных", "testing": "тестовых"}
        label = label_map.get(status_filter, "")
        await message.reply_text(
            f"📋 {label.capitalize() if label else ''} клиентов: <b>0</b>",
            parse_mode="HTML"
        )
        return

    label_map = {"paid": "ОПЛАЧЕННЫЕ", "unpaid": "НЕОПЛАЧЕННЫЕ", "testing": "ТЕСТОВЫЕ"}
    label = label_map.get(status_filter, "ВСЕ")
    header = f"📋 <b>Клиенты [{label}]: {len(clients)}</b>\n\n"

    text = header
    for c in clients:
        entry = format_client(c)
        if len(text) + len(entry) > 3800:
            await message.reply_text(text, parse_mode="HTML")
            text = ""
        text += entry + "\n\n"

    buttons = []
    for c in clients[:8]:
        name = c.get("pc_name", c["client_id"][:12])
        cid = c["client_id"]
        status = c.get("status", "")
        row = []
        if status != "paid":
            row.append(InlineKeyboardButton(
                f"✅ Оплачен: {name[:12]}", callback_data=f"pay_{cid}"
            ))
        if status != "unpaid":
            row.append(InlineKeyboardButton(
                f"❌ Не оплачен: {name[:12]}", callback_data=f"unpay_{cid}"
            ))
        if row:
            buttons.append(row)
        # Rollback button for testing/unpaid clients
        if status in ("testing", "unpaid"):
            buttons.append([InlineKeyboardButton(
                f"⏪ Откат: {name[:12]}", callback_data=f"rollback_{cid}"
            )])

    buttons.append([
        InlineKeyboardButton("🔄 Обновить", callback_data=f"list_{status_filter or 'all'}")
    ])

    await message.reply_text(
        text, parse_mode="HTML",
        reply_markup=InlineKeyboardMarkup(buttons)
    )


async def _send_stats(message):
    all_clients = api_get_clients()
    paid = [c for c in all_clients if c.get("status") == "paid"]
    unpaid = [c for c in all_clients if c.get("status") == "unpaid"]
    testing = [c for c in all_clients if c.get("status") == "testing"]

    online = 0
    now = datetime.utcnow()
    for c in all_clients:
        if c.get("last_seen"):
            try:
                seen = datetime.strptime(c["last_seen"], "%Y-%m-%d %H:%M:%S")
                if (now - seen).total_seconds() < 600:
                    online += 1
            except ValueError:
                pass

    await message.reply_text(
        f"📊 <b>WinFlow — Статистика</b>\n\n"
        f"👥 Всего клиентов: <b>{len(all_clients)}</b>\n"
        f"💰 Оплаченных: <b>{len(paid)}</b>\n"
        f"⏳ Неоплаченных: <b>{len(unpaid)}</b>\n"
        f"🧪 Тестовых: <b>{len(testing)}</b>\n"
        f"🟢 Онлайн (10 мин): <b>{online}</b>\n\n"
        f"👤 Админов: <b>{len(ADMIN_IDS)}</b> ({len(BASE_ADMIN_IDS)} базовых + {len(ADMIN_IDS) - len(BASE_ADMIN_IDS)} динамических)",
        parse_mode="HTML"
    )


# =============================================
# УМНЫЕ УВЕДОМЛЕНИЯ ОБ ОТКАТЕ (Background Job)
# =============================================

def get_notification_interval(remaining_seconds: float) -> float:
    """Динамические интервалы уведомлений."""
    remaining_min = remaining_seconds / 60
    if remaining_min > 120:      # > 2 часа
        return 3600              # каждый час
    elif remaining_min > 60:     # 1-2 часа
        return 1200              # каждые 20 мин
    elif remaining_min > 15:     # 15-60 мин
        return 600               # каждые 10 мин
    elif remaining_min > 5:      # 5-15 мин
        return 300               # каждые 5 мин
    else:                        # < 5 мин
        return 120               # каждые 2 мин


async def send_to_all_admins(context, text, parse_mode="HTML", reply_markup=None):
    """Отправить сообщение ВСЕМ админам."""
    for admin_id in ADMIN_IDS:
        try:
            await context.bot.send_message(
                chat_id=admin_id,
                text=text,
                parse_mode=parse_mode,
                reply_markup=reply_markup
            )
        except Exception as e:
            log.warning(f"Failed to send to admin {admin_id}: {e}")


# Множество client_id для которых уже отправлено "ДЕДЛАЙН ИСТЁК" (не спамить повторно)
_deadline_expired_notified = set()


async def check_rollback_deadlines(context: ContextTypes.DEFAULT_TYPE):
    """Фоновая проверка дедлайнов отката — каждые 60 сек."""
    try:
        clients = api_get_clients()
        # Только testing и unpaid
        targets = [c for c in clients if c.get("status") in ("testing", "unpaid")]
        now = datetime.utcnow()

        # Очистка: если клиент стал paid или удалён — убрать из notified
        active_ids = {c.get("client_id") for c in targets}
        _deadline_expired_notified.intersection_update(active_ids)

        for client in targets:
            deadline_str = client.get("rollback_deadline", "")
            if not deadline_str:
                continue

            try:
                deadline = datetime.strptime(deadline_str, "%Y-%m-%d %H:%M:%S")
            except ValueError:
                continue

            remaining = (deadline - now).total_seconds()
            cid = client["client_id"]

            if remaining <= 0:
                # Дедлайн прошёл — отправить ОДИН раз
                if cid in _deadline_expired_notified:
                    continue  # Уже отправляли, не спамить

                _deadline_expired_notified.add(cid)

                # Ставимо pending_action для автоматичного rollback
                api_set_pending_action(cid, "rollback_cleanup")

                await send_to_all_admins(
                    context,
                    text=(
                        f"🔴 <b>ДЕДЛАЙН ИСТЁК!</b>\n\n"
                        f"🖥 {client.get('pc_name', '?')}\n"
                        f"🆔 <code>{cid}</code>\n"
                        f"💰 Статус: НЕ ОПЛАЧЕН\n\n"
                        f"Система будет откачена автоматически."
                    ),
                    reply_markup=InlineKeyboardMarkup([
                        [InlineKeyboardButton(f"✅ Оплачен", callback_data=f"pay_{cid}")],
                        [InlineKeyboardButton(f"⏪ Откат сейчас", callback_data=f"rollback_{cid}")]
                    ])
                )
                api_update_notification(cid)
                continue

            # Проверяем нужно ли слать уведомление
            interval = get_notification_interval(remaining)

            last_notif_str = client.get("last_notification", "")
            if last_notif_str:
                try:
                    last_notif = datetime.strptime(last_notif_str, "%Y-%m-%d %H:%M:%S")
                    elapsed = (now - last_notif).total_seconds()
                    if elapsed < interval:
                        continue  # Ещё рано
                except ValueError:
                    pass

            # Отправляем уведомление ВСЕМ админам
            hours = int(remaining // 3600)
            minutes = int((remaining % 3600) // 60)

            if hours > 0:
                time_str = f"{hours} час {minutes} мин"
            else:
                time_str = f"{minutes} мин"

            urgency = ""
            if remaining < 900:       # < 15 мин
                urgency = "⚠️ СРОЧНО! "
            elif remaining < 3600:    # < 1 час
                urgency = "⏰ "

            await send_to_all_admins(
                context,
                text=(
                    f"{urgency}⏪ <b>Откат через {time_str}</b>\n\n"
                    f"🖥 {client.get('pc_name', '?')}\n"
                    f"🆔 <code>{cid}</code>\n"
                    f"💰 Статус: НЕ ОПЛАЧЕН\n\n"
                    f"Нажмите ✅ Оплачен для подтверждения."
                ),
                reply_markup=InlineKeyboardMarkup([
                    [InlineKeyboardButton(f"✅ Оплачен", callback_data=f"pay_{cid}")],
                    [InlineKeyboardButton(f"⏪ Откат сейчас", callback_data=f"rollback_{cid}")]
                ])
            )
            api_update_notification(cid)

    except Exception as e:
        log.error(f"Rollback check error: {e}")


# =============================================
# КОМАНДЫ
# =============================================

async def cmd_start(update: Update, ctx: ContextTypes.DEFAULT_TYPE):
    user = update.effective_user
    if not is_admin(user.id, user.username):
        await update.message.reply_text("⛔ Доступ запрещён.")
        return
    await update.message.reply_text(
        "🖥 <b>WinFlow — Панель управления</b>\n\nИспользуйте кнопки внизу 👇",
        parse_mode="HTML", reply_markup=MAIN_KEYBOARD
    )


async def cmd_generate(update: Update, ctx: ContextTypes.DEFAULT_TYPE):
    if not is_admin(update.effective_user.id, update.effective_user.username):
        return
    token = generate_token()
    now = datetime.now(timezone.utc).strftime("%d.%m.%Y %H:%M UTC")

    if token == "ERROR":
        await update.message.reply_text("❌ Ошибка генерации токена. Проверьте API.")
        return

    await update.message.reply_text(
        f"🔑 <b>Новый токен:</b>\n\n"
        f"<code>{token}</code>\n\n"
        f"⏱ Действителен: {TOKEN_LIFETIME_HOURS} часов\n"
        f"📅 Сгенерирован: {now}",
        parse_mode="HTML"
    )


async def cmd_clients(update: Update, ctx: ContextTypes.DEFAULT_TYPE):
    if not is_admin(update.effective_user.id, update.effective_user.username):
        return
    await _send_clients_list(update.message, None)


async def cmd_paid(update: Update, ctx: ContextTypes.DEFAULT_TYPE):
    if not is_admin(update.effective_user.id, update.effective_user.username):
        return
    await _send_clients_list(update.message, "paid")


async def cmd_unpaid(update: Update, ctx: ContextTypes.DEFAULT_TYPE):
    if not is_admin(update.effective_user.id, update.effective_user.username):
        return
    await _send_clients_list(update.message, "unpaid")


async def cmd_testing(update: Update, ctx: ContextTypes.DEFAULT_TYPE):
    if not is_admin(update.effective_user.id, update.effective_user.username):
        return
    await _send_clients_list(update.message, "testing")


async def cmd_mark_paid(update: Update, ctx: ContextTypes.DEFAULT_TYPE):
    if not is_admin(update.effective_user.id, update.effective_user.username):
        return
    if not ctx.args:
        clients = api_get_clients()
        not_paid = [c for c in clients if c.get("status") != "paid"]
        if not not_paid:
            await update.message.reply_text("✅ Все клиенты уже оплачены.")
            return
        buttons = [[InlineKeyboardButton(
            f"✅ {c.get('pc_name', c['client_id'][:12])}",
            callback_data=f"pay_{c['client_id']}"
        )] for c in not_paid]
        await update.message.reply_text(
            "Выберите ID для отметки <b>ОПЛАЧЕН</b>:",
            parse_mode="HTML", reply_markup=InlineKeyboardMarkup(buttons)
        )
        return
    cid = ctx.args[0]
    ok = api_update_status(cid, "paid")
    if ok:
        await update.message.reply_text(f"✅ <code>{cid}</code> → <b>ОПЛАЧЕН</b>", parse_mode="HTML")
    else:
        await update.message.reply_text(f"❌ Ошибка для <code>{cid}</code>", parse_mode="HTML")


async def cmd_mark_unpaid(update: Update, ctx: ContextTypes.DEFAULT_TYPE):
    if not is_admin(update.effective_user.id, update.effective_user.username):
        return
    if not ctx.args:
        clients = api_get_clients()
        not_unpaid = [c for c in clients if c.get("status") != "unpaid"]
        if not not_unpaid:
            await update.message.reply_text("❌ Все клиенты уже неоплачены.")
            return
        buttons = [[InlineKeyboardButton(
            f"❌ {c.get('pc_name', c['client_id'][:12])}",
            callback_data=f"unpay_{c['client_id']}"
        )] for c in not_unpaid]
        await update.message.reply_text(
            "Выберите ID для отметки <b>НЕ ОПЛАЧЕН</b>:",
            parse_mode="HTML", reply_markup=InlineKeyboardMarkup(buttons)
        )
        return
    cid = ctx.args[0]
    ok = api_update_status(cid, "unpaid")
    if ok:
        await update.message.reply_text(f"❌ <code>{cid}</code> → <b>НЕ ОПЛАЧЕН</b>", parse_mode="HTML")
    else:
        await update.message.reply_text(f"❌ Ошибка для <code>{cid}</code>", parse_mode="HTML")


async def cmd_delete(update: Update, ctx: ContextTypes.DEFAULT_TYPE):
    if not is_admin(update.effective_user.id, update.effective_user.username):
        return
    if not ctx.args:
        await update.message.reply_text("🗑 Использование: /delete &lt;id&gt;", parse_mode="HTML")
        return
    cid = ctx.args[0]
    ok = api_delete_client(cid)
    if ok:
        await update.message.reply_text(f"🗑 ID <code>{cid}</code> удалён.", parse_mode="HTML")
    else:
        await update.message.reply_text(f"❌ Не удалось удалить <code>{cid}</code>.", parse_mode="HTML")


async def cmd_stats(update: Update, ctx: ContextTypes.DEFAULT_TYPE):
    if not is_admin(update.effective_user.id, update.effective_user.username):
        return
    await _send_stats(update.message)


async def cmd_rollback(update: Update, ctx: ContextTypes.DEFAULT_TYPE):
    """Ручной откат клиента."""
    if not is_admin(update.effective_user.id, update.effective_user.username):
        return
    if not ctx.args:
        clients = api_get_clients()
        targets = [c for c in clients if c.get("status") in ("testing", "unpaid")]
        if not targets:
            await update.message.reply_text("📋 Нет ID для отката.")
            return
        buttons = [[InlineKeyboardButton(
            f"⏪ {c.get('pc_name', c['client_id'][:12])}",
            callback_data=f"rollback_{c['client_id']}"
        )] for c in targets]
        await update.message.reply_text(
            "⏪ Выберите ID для <b>ОТКАТА</b>:",
            parse_mode="HTML", reply_markup=InlineKeyboardMarkup(buttons)
        )
        return
    cid = ctx.args[0]
    api_set_pending_action(cid, "rollback_cleanup")
    await update.message.reply_text(
        f"⏪ <b>ROLLBACK</b> запущен для <code>{cid}</code>\n"
        f"Агент выполнит откат в течение 30 секунд.",
        parse_mode="HTML"
    )


async def cmd_help(update: Update, ctx: ContextTypes.DEFAULT_TYPE):
    if not is_admin(update.effective_user.id, update.effective_user.username):
        return
    await update.message.reply_text(
        "📖 <b>WinFlow Bot — Команды</b>\n\n"
        "🔑 /generate — Новый токен\n"
        "📋 /clients — Все клиенты\n"
        "💰 /paid — Оплаченные\n"
        "⏳ /unpaid — Неоплаченные\n"
        "🧪 /testing — Тестовые\n"
        "✅ /mark_paid &lt;id&gt; — Пометить оплаченным\n"
        "❌ /mark_unpaid &lt;id&gt; — Пометить неоплаченным\n"
        "⏪ /rollback &lt;id&gt; — Откатить\n"
        "🗑 /delete &lt;id&gt; — Удалить\n"
        "📊 /stats — Статистика\n"
        "👤 /add_admin — Додати адміна\n"
        "🗑 /remove_admin — Видалити адміна\n"
        "📋 /admins — Список адмінів\n"
        "❓ /help — Эта справка\n\n"
        "💡 Используйте кнопки внизу 👇\n"
        "⏰ Уведомления об откате — автоматические",
        parse_mode="HTML"
    )


async def cmd_add_admin(update: Update, ctx: ContextTypes.DEFAULT_TYPE):
    """Додати нового адміна."""
    if not is_admin(update.effective_user.id, update.effective_user.username):
        return

    if ctx.args:
        # Якщо передано аргумент — додати одразу
        try:
            new_id = int(ctx.args[0])
            if add_admin(new_id):
                await update.message.reply_text(
                    f"✅ Админ <code>{new_id}</code> добавлен!\n"
                    f"Теперь он будет получать уведомления об откате.",
                    parse_mode="HTML"
                )
            else:
                await update.message.reply_text(
                    f"ℹ️ <code>{new_id}</code> уже есть в списке админов.",
                    parse_mode="HTML"
                )
        except ValueError:
            await update.message.reply_text("❌ Chat ID должен быть числом.")
        return

    # Без аргумента — установить состояние ожидания
    _awaiting_admin_add[update.effective_user.id] = True
    await update.message.reply_text(
        "👤 <b>Добавить админа</b>\n\n"
        "Введите Chat ID нового админа:\n\n"
        "💡 Chat ID можно узнать:\n"
        "• Через бота @userinfobot — откройте его и нажмите /start\n"
        "• Через бота @getmyid_bot — он покажет ваш ID\n"
        "• Или перешлите любое сообщение от нужного человека боту @userinfobot\n\n"
        "📝 Введите число (Chat ID):",
        parse_mode="HTML"
    )


async def cmd_remove_admin(update: Update, ctx: ContextTypes.DEFAULT_TYPE):
    """Видалити адміна."""
    if not is_admin(update.effective_user.id, update.effective_user.username):
        return

    dynamic_admins = [aid for aid in ADMIN_IDS if aid not in BASE_ADMIN_IDS]

    if not dynamic_admins:
        await update.message.reply_text(
            "📋 Нет динамических админов для удаления.\n"
            "Базовых админов удалить нельзя.",
            parse_mode="HTML"
        )
        return

    buttons = []
    for aid in dynamic_admins:
        buttons.append([InlineKeyboardButton(
            f"🗑 Удалить {aid}",
            callback_data=f"rmadmin_{aid}"
        )])

    await update.message.reply_text(
        "🗑 <b>Удалить админа</b>\n\n"
        "Выберите админа для удаления:\n"
        "(Базовых админов удалить нельзя)",
        parse_mode="HTML",
        reply_markup=InlineKeyboardMarkup(buttons)
    )


async def cmd_admins(update: Update, ctx: ContextTypes.DEFAULT_TYPE):
    """Список всіх адмінів."""
    if not is_admin(update.effective_user.id, update.effective_user.username):
        return

    lines = ["👥 <b>Список админов:</b>\n"]
    for aid in ADMIN_IDS:
        marker = "🔒" if aid in BASE_ADMIN_IDS else "👤"
        label = "базовый" if aid in BASE_ADMIN_IDS else "динамический"
        lines.append(f"{marker} <code>{aid}</code> ({label})")

    await update.message.reply_text("\n".join(lines), parse_mode="HTML")


# =============================================
# ТЕКСТОВЫЕ КНОПКИ (Reply Keyboard)
# =============================================

async def handle_text(update: Update, ctx: ContextTypes.DEFAULT_TYPE):
    if not is_admin(update.effective_user.id, update.effective_user.username):
        return

    user_id = update.effective_user.id
    text = update.message.text.strip()

    # Перевірити чи очікуємо введення Chat ID для додавання адміна
    if _awaiting_admin_add.get(user_id):
        del _awaiting_admin_add[user_id]

        # Спроба парсити Chat ID
        try:
            new_id = int(text)
            if add_admin(new_id):
                await update.message.reply_text(
                    f"✅ Админ <code>{new_id}</code> добавлен!\n"
                    f"Теперь он будет получать уведомления об откате.\n\n"
                    f"⚠️ Важно: новый админ должен нажать /start в этом боте, "
                    f"чтобы бот мог отправлять ему сообщения.",
                    parse_mode="HTML",
                    reply_markup=MAIN_KEYBOARD
                )
            else:
                await update.message.reply_text(
                    f"ℹ️ <code>{new_id}</code> уже есть в списке админов.",
                    parse_mode="HTML",
                    reply_markup=MAIN_KEYBOARD
                )
        except ValueError:
            await update.message.reply_text(
                "❌ Неверный формат. Chat ID должен быть числом.\n"
                "Попробуйте ещё раз: нажмите 👤 Добавить админа",
                reply_markup=MAIN_KEYBOARD
            )
        return

    # Стандартні кнопки
    if text == "🔑 Новый токен":
        await cmd_generate(update, ctx)
    elif text == "📋 Все клиенты":
        await _send_clients_list(update.message, None)
    elif text == "💰 Оплаченные":
        await _send_clients_list(update.message, "paid")
    elif text == "⏳ Неоплаченные":
        await _send_clients_list(update.message, "unpaid")
    elif text == "🧪 Тестовые":
        await _send_clients_list(update.message, "testing")
    elif text == "📊 Статистика":
        await _send_stats(update.message)
    elif text == "👤 Добавить админа":
        await cmd_add_admin(update, ctx)
    elif text == "❓ Помощь":
        await cmd_help(update, ctx)


# =============================================
# CALLBACK (INLINE КНОПКИ)
# =============================================

async def callback_handler(update: Update, ctx: ContextTypes.DEFAULT_TYPE):
    query = update.callback_query
    await query.answer()
    if not is_admin(query.from_user.id, query.from_user.username):
        return

    data = query.data

    if data.startswith("pay_"):
        cid = data[4:]
        ok = api_update_status(cid, "paid")
        if ok:
            # При оплаті — ставимо pending_action для видалення старої партиції
            api_set_pending_action(cid, "payment_cleanup")
            await query.message.reply_text(
                f"✅ ID <code>{cid}</code> → <b>ОПЛАЧЕН</b>\n"
                f"🗑 Старый раздел будет удалён автоматически.",
                parse_mode="HTML"
            )
        else:
            await query.message.reply_text("❌ Ошибка обновления.")

    elif data.startswith("unpay_"):
        cid = data[6:]
        ok = api_update_status(cid, "unpaid")
        if ok:
            await query.message.reply_text(
                f"❌ ID <code>{cid}</code> → <b>НЕ ОПЛАЧЕН</b>",
                parse_mode="HTML"
            )
        else:
            await query.message.reply_text("❌ Ошибка обновления.")

    elif data.startswith("rollback_"):
        cid = data[9:]
        api_set_pending_action(cid, "rollback_cleanup")
        await query.message.reply_text(
            f"⏪ <b>ROLLBACK</b> запущен для ID <code>{cid}</code>\n"
            f"Агент выполнит откат в течение 30 секунд.",
            parse_mode="HTML"
        )

    elif data.startswith("rmadmin_"):
        aid_str = data[8:]
        try:
            aid = int(aid_str)
            if remove_admin(aid):
                await query.message.reply_text(
                    f"✅ Админ <code>{aid}</code> удалён.",
                    parse_mode="HTML"
                )
            else:
                await query.message.reply_text(
                    f"❌ Не удалось удалить <code>{aid}</code>.\n"
                    f"Базовых админов удалить нельзя.",
                    parse_mode="HTML"
                )
        except ValueError:
            await query.message.reply_text("❌ Ошибка парсинга ID.")

    elif data.startswith("info_"):
        cid = data[5:]
        clients = api_get_clients()
        client = next((c for c in clients if c.get("client_id") == cid), None)
        if client:
            await query.message.reply_text(format_client(client), parse_mode="HTML")
        else:
            await query.message.reply_text(f"❓ ID <code>{cid}</code> не найден.", parse_mode="HTML")

    elif data.startswith("refresh_"):
        cid = data[8:]
        clients = api_get_clients()
        client = next((c for c in clients if c.get("client_id") == cid), None)
        if client:
            # Проверка онлайн
            online = "🔴 Оффлайн"
            if client.get("last_seen"):
                try:
                    seen = datetime.strptime(client["last_seen"], "%Y-%m-%d %H:%M:%S")
                    if (datetime.utcnow() - seen).total_seconds() < 600:
                        online = "🟢 Онлайн"
                except ValueError:
                    pass
            await query.message.reply_text(
                f"🔄 <b>Обновлено:</b> {client.get('pc_name', '?')}\n"
                f"🆔 <code>{cid}</code>\n"
                f"💰 Статус: {client.get('status', '?').upper()}\n"
                f"{online}\n"
                f"🕐 Последний раз: {client.get('last_seen', 'неизвестно')}",
                parse_mode="HTML"
            )
        else:
            await query.message.reply_text(f"❓ ID <code>{cid}</code> не найден.", parse_mode="HTML")

    elif data.startswith("status_"):
        cid = data[7:]
        clients = api_get_clients()
        client = next((c for c in clients if c.get("client_id") == cid), None)
        if client:
            status = client.get("status", "unknown")
            emoji = {"paid": "💰", "unpaid": "⏳", "testing": "🧪"}.get(status, "❓")
            await query.message.reply_text(
                f"{emoji} <b>Статус оплаты:</b> {status.upper()}\n"
                f"🖥 {client.get('pc_name', '?')}\n"
                f"🆔 <code>{cid}</code>",
                parse_mode="HTML"
            )
        else:
            await query.message.reply_text(f"❓ ID <code>{cid}</code> не найден.", parse_mode="HTML")

    elif data.startswith("list_"):
        flt = data[5:]
        status_filter = None if flt == "all" else flt
        await _send_clients_list(query.message, status_filter)


# =============================================
# MAIN
# =============================================

def main():
    log.info("Starting WinFlow Bot...")

    # Завантажити динамічних адмінів
    load_dynamic_admins()
    log.info(f"Total admins: {len(ADMIN_IDS)} (base: {len(BASE_ADMIN_IDS)})")

    app = Application.builder().token(BOT_TOKEN).build()

    # Команды
    app.add_handler(CommandHandler("start", cmd_start))
    app.add_handler(CommandHandler("generate", cmd_generate))
    app.add_handler(CommandHandler("clients", cmd_clients))
    app.add_handler(CommandHandler("paid", cmd_paid))
    app.add_handler(CommandHandler("unpaid", cmd_unpaid))
    app.add_handler(CommandHandler("testing", cmd_testing))
    app.add_handler(CommandHandler("mark_paid", cmd_mark_paid))
    app.add_handler(CommandHandler("mark_unpaid", cmd_mark_unpaid))
    app.add_handler(CommandHandler("delete", cmd_delete))
    app.add_handler(CommandHandler("rollback", cmd_rollback))
    app.add_handler(CommandHandler("stats", cmd_stats))
    app.add_handler(CommandHandler("add_admin", cmd_add_admin))
    app.add_handler(CommandHandler("remove_admin", cmd_remove_admin))
    app.add_handler(CommandHandler("admins", cmd_admins))
    app.add_handler(CommandHandler("help", cmd_help))

    # Текстовые кнопки
    app.add_handler(MessageHandler(filters.TEXT & ~filters.COMMAND, handle_text))

    # Inline кнопки
    app.add_handler(CallbackQueryHandler(callback_handler))

    # Фоновая проверка дедлайнов отката (каждые 60 сек)
    app.job_queue.run_repeating(
        check_rollback_deadlines,
        interval=60,
        first=10
    )

    log.info("Bot started. Polling...")
    app.run_polling(drop_pending_updates=True)


if __name__ == "__main__":
    main()
