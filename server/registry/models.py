import uuid
from django.db import models

class Verifier(models.Model):
    github_id = models.PositiveBigIntegerField(unique=True)
    github_login = models.CharField(max_length=100)
    created_at = models.DateTimeField(auto_now_add=True)

class SigningKey(models.Model):
    fingerprint = models.CharField(max_length=64, primary_key=True)
    verifier = models.ForeignKey(Verifier, on_delete=models.PROTECT, related_name='keys')
    public_key = models.CharField(max_length=100)
    proof_url = models.URLField(max_length=300)
    proof = models.JSONField()
    created_at = models.DateTimeField(auto_now_add=True)
    revoked_at = models.DateTimeField(null=True, blank=True)
    revocation = models.JSONField(null=True, blank=True)

class Mod(models.Model):
    platform = models.CharField(max_length=32)
    platform_id = models.CharField(max_length=80)
    name = models.CharField(max_length=160)
    created_at = models.DateTimeField(auto_now_add=True)
    class Meta:
        constraints = [models.UniqueConstraint(fields=['platform', 'platform_id'], name='unique_mod')]

class Release(models.Model):
    id = models.UUIDField(primary_key=True, default=uuid.uuid4, editable=False)
    mod = models.ForeignKey(Mod, on_delete=models.PROTECT, related_name='releases')
    version = models.CharField(max_length=80)
    manifest_hash = models.CharField(max_length=64)
    manifest = models.JSONField()
    created_at = models.DateTimeField(auto_now_add=True)
    class Meta:
        constraints = [models.UniqueConstraint(fields=['mod', 'version', 'manifest_hash'], name='unique_release_claim')]

class Attestation(models.Model):
    id = models.UUIDField(primary_key=True, default=uuid.uuid4, editable=False)
    release = models.ForeignKey(Release, on_delete=models.PROTECT, related_name='attestations')
    key = models.ForeignKey(SigningKey, on_delete=models.PROTECT, related_name='attestations')
    result = models.CharField(max_length=20, choices=[('published','Published'), ('reproduced','Reproduced'), ('mismatch','Mismatch')])
    payload = models.TextField()
    signature = models.CharField(max_length=100)
    payload_hash = models.CharField(max_length=64, unique=True)
    created_at = models.DateTimeField(auto_now_add=True)
