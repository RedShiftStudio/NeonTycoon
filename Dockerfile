FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /source
COPY NeonTycoon.slnx ./
COPY src/NeonTycoon.Domain/*.csproj src/NeonTycoon.Domain/
COPY src/NeonTycoon.Application/*.csproj src/NeonTycoon.Application/
COPY src/NeonTycoon.Infrastructure/*.csproj src/NeonTycoon.Infrastructure/
COPY src/NeonTycoon.Api/*.csproj src/NeonTycoon.Api/
RUN dotnet restore
COPY src/ src/
RUN dotnet publish src/NeonTycoon.Api -c Release --no-restore -o /publish
FROM mcr.microsoft.com/dotnet/aspnet:10.0
RUN apt-get update && apt-get install -y --no-install-recommends curl && rm -rf /var/lib/apt/lists/*
WORKDIR /app
COPY --from=build /publish ./
USER $APP_UID
ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080
ENTRYPOINT ["dotnet","NeonTycoon.Api.dll"]
