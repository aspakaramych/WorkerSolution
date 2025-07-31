# Worker Solution

Учебный проект по запуску docker контейнеров из кода и работой с rabbitmq.

Сейчас готовы запуск python, js и C# кода, с++ в разработке.

1. Перед тем как запустить надо запуллить docker образы, в которых будет запускаться код: 

`docker pull python:3.9-slim-buster && docker pull  mcr.microsoft.com/dotnet/sdk:8.0 && docker pull node:18-slim && docker pull gcc:latest`

2. Перейти в директорию проекта и выполнить:
`docker compose up --build`
