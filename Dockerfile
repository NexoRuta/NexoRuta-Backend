ARG RUNTIME_IMAGE=mcr.microsoft.com/dotnet/aspnet:10.0

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build

ARG PROJECT=src/NexoRuta.Api/NexoRuta.Api.csproj

WORKDIR /src
COPY . .

RUN dotnet restore "$PROJECT"
RUN dotnet publish "$PROJECT" -c Release -o /app/publish /p:UseAppHost=false

FROM ${RUNTIME_IMAGE} AS final

ARG RUNTIME_TOOLS=false
RUN if [ "$RUNTIME_TOOLS" = "true" ]; then \
        apt-get update \
        && apt-get install -y --no-install-recommends curl libgssapi-krb5-2 \
        && rm -rf /var/lib/apt/lists/*; \
    fi

WORKDIR /app
COPY --from=build /app/publish .

ENV ASPNETCORE_URLS=http://+:8080

EXPOSE 8080

ENTRYPOINT ["dotnet", "NexoRuta.Api.dll"]
