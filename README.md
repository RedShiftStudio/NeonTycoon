# NEON TYCOON

Telegram Mini App: мобильный cyberpunk idle tycoon. Игрок строит цифровой мегаполис, добывает NEON Credits, исследует технологии и соревнуется за позицию в рейтинге. Исходники содержат backend, веб-клиент и production-инфраструктуру; реальные Bot Token, домен и пароли предоставляются оператором.

## Структура и технологии

- `src/NeonTycoon.Domain` — сущности и формулы экономики.
- `src/NeonTycoon.Application` — DTO и контракты.
- `src/NeonTycoon.Infrastructure` — EF Core 10, PostgreSQL, миграции, каталог, транзакционная игровая логика.
- `src/NeonTycoon.Api` — ASP.NET Core 10 Minimal API, Telegram HMAC, JWT, Serilog, Redis, rate limiting, OpenAPI, health checks, фоновые события.
- `src/neon-tycoon-web` — React 19, TypeScript, Vite, Tailwind CSS 4, Motion, Zustand, TanStack Query, Lucide.
- `Dockerfile`, `docker-compose.yml`, `docker-compose.prod.yml` — образы, PostgreSQL, Redis, HTTPS Caddy.

## Экономика

12 зданий в трёх районах; покупка `baseCost × 1.18^count`, улучшение `baseCost × 2.8^level`, производство `baseProduction × count × (1 + level × 0.5)` с бонусами исследований, престижа и событий. Активная награда зависит от уровня, технологий и престижа; каждый тап тратит 1 Energy, Energy восстанавливается по 1 за 10 секунд. Ограничение тапов — 180 мс и общий rate limit. Серверная прибыль начисляется по timestamp в сериализуемой транзакции: максимум 8 часов (12 с Night shift), повторное начисление невозможно. Опыт от заработка, новые уровни открывают здания и исследования. Престиж с уровня 20 и 1M заработанных кредитов сбрасывает постройки и технологии, оставляя постоянный бонус +15% за цикл. Есть задания с дневным/недельным сбросом, достижения, награды за вход со streak до 7 дней, рефералы с наградой 500 и еженедельное событие Neon Surge (+50% производства в первые два дня недели).

Балансы и награды рассчитывает только сервер. Состояние сохраняется в PostgreSQL. Денежные величины — `decimal(28,2)` с верхним пределом; клиент не передаёт цены. Уникальные индексы препятствуют повторному получению награды и реферальному злоупотреблению. Сериализуемые транзакции предотвращают конкурентное двойное списание; при конфликте сервер возвращает HTTP 409. Лимиты ставок и проверка Energy ограничивают автоматизированные действия.

## Настройка Telegram

1. Создайте бота через @BotFather (`/newbot`), сохраните token **только** в `.env` на сервере.
2. Настройте Mini App / Menu Button на `https://ВАШ_ДОМЕН/`, домен должен совпадать с `PUBLIC_ORIGIN`.
3. Укажите username бота в `VITE_BOT_USERNAME` без `@`. Ссылка приглашения формируется как `https://t.me/USERNAME?startapp=CODE`.
4. WebApp SDK передаёт `initData`; backend проверяет HMAC-SHA256 по официальному алгоритму Telegram, TTL `auth_date` 1 час, и затем выдаёт JWT на 12 часов. Вне Telegram авторизация не работает. Секреты никогда не встраиваются в frontend.

## Переменные окружения

Скопируйте `.env.example` в `.env` и замените значения: `POSTGRES_USER`, `POSTGRES_PASSWORD`, `POSTGRES_DB`, `JWT_SECRET` (минимум 32 байта), `BOT_TOKEN`, `PUBLIC_ORIGIN` (точный HTTPS origin), `DOMAIN` (публичный домен для Caddy), `VITE_BOT_USERNAME` (compile-time build arg). Для локального запуска backend нужны `ConnectionStrings__Game` (Npgsql connection string) и `REDIS` (`host:6379`). Не публикуйте `.env`. Укажите DNS A/AAAA домена на VPS и откройте 80/443 для автоматического ACME HTTPS Caddy.

## Развёртывание

Нужны Docker + Compose, доступный DNS, рабочие Bot Token и Mini App URL. Перед первым запуском и каждым деплоем:

```bash
cp .env.example .env
# заполните .env
# сервисы БД и Redis
 docker compose -f docker-compose.yml -f docker-compose.prod.yml up -d postgres redis
# одноразовая deployment-команда: миграции + идемпотентное заполнение каталогов
 docker compose -f docker-compose.yml -f docker-compose.prod.yml run --build --rm api --migrate
 docker compose -f docker-compose.yml -f docker-compose.prod.yml up -d --build api web
```

Миграции не запускаются автоматически в web-процессе. Для разработки схема создаётся командой `dotnet ef database update --project src/NeonTycoon.Infrastructure --startup-project src/NeonTycoon.Infrastructure`, а каталоги — командой `dotnet run --project src/NeonTycoon.Api -- --migrate` (с настроенным окружением). Health endpoints: `/health/live`, `/health/ready`. OpenAPI: `/openapi/v1.json` (JWT).

## Сборка без тестов

```bash
dotnet restore
dotnet build -c Release
cd src/neon-tycoon-web
npm ci
npm run build
```

Для изменения схемы: `dotnet ef migrations add NAME --project src/NeonTycoon.Infrastructure --startup-project src/NeonTycoon.Infrastructure --output-dir Migrations`. Файл миграции хранится в репозитории.

## Резервное копирование PostgreSQL

```bash
docker compose exec -T postgres pg_dump -U "$POSTGRES_USER" -Fc "$POSTGRES_DB" > neon-backup.dump
cat neon-backup.dump | docker compose exec -T postgres pg_restore -U "$POSTGRES_USER" -d "$POSTGRES_DB" --clean --if-exists
```

Команды с `$POSTGRES_USER` выполняйте в окружении с экспортированными переменными из `.env`; при восстановлении остановите пишущие процессы и предварительно сделайте копию текущей базы. Храните резервные копии вне VPS, шифруйте и регулярно ротируйте.

## API

Авторизация: `POST /api/auth/telegram` (`{initData}`), далее заголовок `Authorization: Bearer <token>`. Профиль: `GET /api/player/me`, `GET /api/player/economy`. Экономика: `POST /api/game/tap`, `/collect`, `/prestige`. Постройки: `GET /api/buildings`, `POST /api/buildings/{id}/purchase`, `/upgrade`. Исследования: `GET /api/research`, `POST /api/research/{id}/unlock`. Задания и достижения: `GET /api/missions`, `/achievements`, `POST /api/missions/{id}/claim`, `/achievements/{id}/claim`. Ежедневный вход: `GET /api/daily-rewards`, `POST /api/daily-rewards/claim`. Социальное: `GET /api/leaderboard`, `/referrals`; события: `GET /api/events`. Все игровые маршруты требуют JWT. Все POST-операции после авторизации требуют уникального заголовка `Idempotency-Key: UUID`; Redis хранит ключ 24 часа и отклоняет повтор с HTTP 409. Redis кэширует рейтинг 60 секунд; PostgreSQL хранит все авторитетные данные.


