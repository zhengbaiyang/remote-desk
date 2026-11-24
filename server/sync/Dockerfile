FROM python:3.12-alpine

RUN adduser -D -u 10001 sync && mkdir -p /data && chown -R sync:sync /data

WORKDIR /app
COPY security.py storage.py app.py /app/

USER sync
EXPOSE 11020

CMD ["python", "-u", "/app/app.py"]
