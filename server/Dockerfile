FROM python:3.13-slim
ENV PYTHONDONTWRITEBYTECODE=1 PYTHONUNBUFFERED=1
WORKDIR /app
COPY requirements.txt .
RUN pip install --no-cache-dir -r requirements.txt && useradd --uid 10001 --create-home uvm
COPY . .
RUN UVM_SECRET_KEY=build-only-static-assets python manage.py collectstatic --noinput && chown -R uvm:uvm /app
USER 10001
EXPOSE 8000
HEALTHCHECK --interval=30s --timeout=5s --start-period=45s CMD python -c "import urllib.request; r=urllib.request.Request('http://127.0.0.1:8000/healthz',headers={'X-Forwarded-Proto':'https'}); urllib.request.urlopen(r,timeout=4)"
CMD ["sh", "-c", "python manage.py migrate --noinput && exec gunicorn uvm_registry.wsgi:application --bind 0.0.0.0:8000 --workers 2 --threads 2 --timeout 30 --access-logfile - --access-logformat '%(h)s %(r)s %(s)s %(L)s'"]
