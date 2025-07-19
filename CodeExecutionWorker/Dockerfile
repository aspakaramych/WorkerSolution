FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build
WORKDIR /src
COPY ["CodeExecutionWorker.csproj", "./"]
RUN dotnet restore "CodeExecutionWorker.csproj"
COPY . .
WORKDIR "/src/."
RUN dotnet build "CodeExecutionWorker.csproj" -c Release -o /app/build

FROM build AS publish
RUN dotnet publish "CodeExecutionWorker.csproj" -c Release -o /app/publish /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:9.0 AS final
WORKDIR /app
COPY --from=publish /app/publish .
ENTRYPOINT ["dotnet", "CodeExecutionWorker.dll"]
