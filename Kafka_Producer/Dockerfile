FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src
COPY Kafka_Producer/Kafka_Producer.csproj Kafka_Producer/
RUN dotnet restore Kafka_Producer/Kafka_Producer.csproj
COPY . .
RUN dotnet publish Kafka_Producer/Kafka_Producer.csproj -c Release -o /app/publish

FROM mcr.microsoft.com/dotnet/runtime:8.0
WORKDIR /app
COPY --from=build /app/publish .
ENV DOTNET_EnableDiagnostics=0
ENTRYPOINT ["dotnet","Kafka_Producer.dll"]
