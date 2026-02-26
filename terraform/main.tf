terraform {
  required_version = ">= 1.5.0"
  required_providers {
    google = {
      source  = "hashicorp/google"
      version = "~> 5.0"
    }
  }
}

variable "project_id" { type = string }
variable "region" { type = string }
variable "bucket_name" { type = string }

provider "google" {
  project = var.project_id
  region  = var.region
}

resource "google_project_service" "tts" { project = var.project_id service = "texttospeech.googleapis.com" }
resource "google_project_service" "storage" { project = var.project_id service = "storage.googleapis.com" }
resource "google_project_service" "iam" { project = var.project_id service = "iam.googleapis.com" }

resource "google_storage_bucket" "long_audio" {
  name                        = var.bucket_name
  location                    = var.region
  uniform_bucket_level_access = true

  lifecycle_rule {
    action { type = "Delete" }
    condition { age = 7 }
  }
}

resource "google_service_account" "modeb" {
  account_id   = "cloudreader-modeb"
  display_name = "CloudScribe Pro Mode B"
}

resource "google_storage_bucket_iam_member" "creator" {
  bucket = google_storage_bucket.long_audio.name
  role   = "roles/storage.objectCreator"
  member = "serviceAccount:${google_service_account.modeb.email}"
}

resource "google_storage_bucket_iam_member" "viewer" {
  bucket = google_storage_bucket.long_audio.name
  role   = "roles/storage.objectViewer"
  member = "serviceAccount:${google_service_account.modeb.email}"
}

output "bucket_name" { value = google_storage_bucket.long_audio.name }
output "service_account_email" { value = google_service_account.modeb.email }
output "google_application_credentials_template" {
  value = "export GOOGLE_APPLICATION_CREDENTIALS=/path/to/${google_service_account.modeb.account_id}.json"
}
