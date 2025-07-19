FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build
WORKDIR /src
COPY ["CodeExecutionService.csproj", "./"]
RUN dotnet restore "CodeExecutionService.csproj"
COPY . .
WORKDIR "/src/."
RUN dotnet build "CodeExecutionService.csproj" -c Release -o /app/build

FROM build AS publish
RUN dotnet publish "CodeExecutionService.csproj" -c Release -o /app/publish /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:9.0 AS final
WORKDIR /app
COPY --from=publish /app/publish .
ENTRYPOINT ["dotnet", "CodeExecutionService.dll"]