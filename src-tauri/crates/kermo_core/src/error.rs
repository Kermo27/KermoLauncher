use crate::url_sanitizer::mask_url;

pub type Result<T> = std::result::Result<T, Error>;

#[derive(Debug, thiserror::Error)]
pub enum Error {
    #[error("sqlite: {0}")]
    Db(#[from] rusqlite::Error),
    #[error("http: {0}")]
    Http(#[from] reqwest::Error),
    #[error("json: {0}")]
    Json(#[from] serde_json::Error),
    #[error("io: {0}")]
    Io(#[from] std::io::Error),
    #[error("http {status} from {url}")]
    HttpStatus { status: u16, url: String },
    #[error("cancelled")]
    Cancelled,
    #[error("{0}")]
    Message(String),
}

impl Error {
    pub fn message(msg: impl Into<String>) -> Self {
        Self::Message(msg.into())
    }

    pub fn http_status(status: reqwest::StatusCode, url: &str) -> Self {
        Self::HttpStatus {
            status: status.as_u16(),
            url: mask_url(url),
        }
    }

    pub fn is_cancelled(&self) -> bool {
        matches!(self, Error::Cancelled)
    }
}
