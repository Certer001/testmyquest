# ModuleDev Week 2 — персистентное workflow-ядро

Продолжение Week 1: generic action runtime + C# workflow worker, lease/fencing, версионированные карты.

## Быстрый запуск

```bash
docker compose up -d --build
curl -s http://localhost:8080/health/ready
./check.sh
```

Отчёт: `week-2-public-report.json` (не коммитить).

## Решение

### Архитектура

| Сервис | Роль |
|---|---|
| **gateway** | Единственная внешняя точка (:8080) |
| **api** | Action runtime, `workflow.get` |
| **cli** | `action` / `flow` / `migration` команды |
| **postgres** | Catalog, workflow state, jobs, signals |
| **worker-a / worker-b** | Общий C# worker image, lease owners |

```
Client → gateway → api → api.invoke()
              ↑              ↑
         workflow.get   worker → claim_jobs → invoke → finish_job
```

- C4: [docs/c4-container.md](docs/c4-container.md)
- ADR lease/fencing: [docs/adr-workflow-lease-fencing.md](docs/adr-workflow-lease-fencing.md)

### Запуск

```bash
docker compose up -d --build
```

Сервисы: `gateway`, `api`, `cli`, `postgres`, `worker-a`, `worker-b`. Миграции применяет одноразовый `migrate`.

### Workflow-карты

```bash
docker compose run --rm cli flow validate /app/maps/workflow-smoke.v1.flow.json
docker compose run --rm cli flow publish /app/maps/workflow-smoke.v1.flow.json
docker compose run --rm cli flow activate workflow-smoke --version 1
docker compose run --rm cli flow start workflow-smoke --business-key demo-1 --data /dev/stdin <<< '{}'
```

Формат: [contracts/course-1/workflow-map.schema.json](contracts/course-1/workflow-map.schema.json)

### Worker

- Роль БД: `workflow_worker` (только `claim_jobs`, `api.invoke`, `finish_job`, `fail_job`)
- Lease + fencing через `lease_version` / `attempt_id`
- Test profile: `COURSE_TEST_PROFILE=1`, lease 2s, poll 100ms

### Проверка

```bash
./check.sh
./check.sh --keep-stack   # оставить стек для диагностики
```

### Диагностика

- `flow get <process-id>` — compact process state
- `POST /api/workflow/get` с policy `workflow:read`
- Views: `autocheck.processes`, `jobs`, `attempts`, `signals`, `workflow_events`

### Ограничения

- Нет BPMN import, parallel gateways, timers
- Manual step только до `WAITING_MANUAL` (завершение — неделя 3)
- Нет migration running process между версиями
