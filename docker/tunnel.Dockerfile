# Офіційний образ cloudflared — distroless (без sh), а entrypoint compose
# потребує sh+tee для логу URL у спільний volume. Тому: alpine + бінарник
# з офіційного образу.
FROM alpine:3.20
COPY --from=cloudflare/cloudflared:latest /usr/local/bin/cloudflared /usr/local/bin/cloudflared
