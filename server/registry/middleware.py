class HeadersMiddleware:
    def __init__(self, get_response): self.get_response = get_response
    def __call__(self, request):
        response = self.get_response(request)
        response['Content-Security-Policy'] = "default-src 'self'; script-src 'self' https://www.googletagmanager.com; style-src 'self'; img-src 'self' data:; connect-src 'self' https://www.google-analytics.com https://region1.google-analytics.com https://analytics.google.com; object-src 'none'; base-uri 'none'; frame-ancestors 'none'; form-action 'self'"
        response['Referrer-Policy'] = 'strict-origin-when-cross-origin'
        response['Permissions-Policy'] = 'camera=(), microphone=(), geolocation=()'
        return response
