FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /source
COPY Chirper.Grains.Interfaces/Chirper.Grains.Interfaces.csproj Chirper.Grains.Interfaces/
COPY Chirper.Grains/Chirper.Grains.csproj Chirper.Grains/
COPY Chirper.Web/Chirper.Web.csproj Chirper.Web/
RUN dotnet restore Chirper.Web/Chirper.Web.csproj
COPY . .
RUN dotnet publish Chirper.Web/Chirper.Web.csproj -c Release --no-restore -o /app
RUN dotnet publish Chirper.Health/Chirper.Health.csproj -c Release -o /health
FROM mcr.microsoft.com/dotnet/aspnet:10.0
ARG REVISION=development
LABEL coolify.managed=true org.opencontainers.image.revision=$REVISION
ENV ASPNETCORE_HTTP_PORTS=8080 DataDirectory=/data APP_REVISION=$REVISION
WORKDIR /app
COPY --from=build /app .
COPY --from=build /health .
USER root
RUN mkdir /data && chown app:app /data
USER app
EXPOSE 8080
ENTRYPOINT ["dotnet", "Chirper.Web.dll"]
