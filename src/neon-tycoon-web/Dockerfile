FROM node:22-alpine AS build
WORKDIR /web
ARG VITE_BOT_USERNAME
ENV VITE_BOT_USERNAME=$VITE_BOT_USERNAME
COPY package*.json ./
RUN npm ci
COPY . .
RUN npm run build
FROM caddy:2-alpine
COPY --from=build /web/dist /srv
COPY Caddyfile /etc/caddy/Caddyfile
