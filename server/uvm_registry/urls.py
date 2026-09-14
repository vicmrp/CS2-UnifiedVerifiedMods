from django.urls import path
from registry import views
urlpatterns = [path('',views.index),path('healthz',views.health),
 path('downloads/<str:filename>',views.download),
 path('api/v1/mods',views.mods),path('api/v1/mods/<str:platform>/<str:mod_id>',views.mod_detail),
 path('api/v1/mods/<str:platform>/<str:mod_id>/releases/<str:version>',views.version_detail),
 path('api/v1/releases/<uuid:release_id>/attestations',views.attestations),
 path('api/v1/keys/challenge',views.key_challenge),path('api/v1/keys',views.register_key),
 path('api/v1/keys/<str:fingerprint>',views.keys),path('api/v1/keys/<str:fingerprint>/revoke',views.revoke_key),
 path('api/v1/attestations',views.submit_attestation),path('api/v1/lookup',views.lookup)]
