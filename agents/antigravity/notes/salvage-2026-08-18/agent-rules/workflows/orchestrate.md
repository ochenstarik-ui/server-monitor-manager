---
description: Запустить ограниченный SDLC-цикл с fail-closed review и доказательствами проверок
---

При `/orchestrate <задача>`:

1. Применить контракты ролей из правила `agents-contracts`, остальные применимые rules и skill `orchestration-workflow`.
2. Сформировать контракт задачи: scope, acceptance criteria, запреты, проверки и бюджет.
3. Выполнить `research? → implementation → deterministic-checks → review → testing`.
4. Для review обязательно применить skill `code-reviewer`. Не использовать `APPROVE`, если diff не прочитан целиком.
5. Не более одного ремонтного цикла обычно и двух для complex-задачи.
6. Завершить отчётом `DONE`, `PARTIAL` или `BLOCKED`, разделив PASS, FAIL, NOT_RUN и BLOCKED.
