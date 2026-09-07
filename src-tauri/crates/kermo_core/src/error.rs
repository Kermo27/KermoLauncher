pub type Result<T> = std::result::Result<T, Error>;

#[derive(Debug, thiserror::Error)]
pub enum Error {
    #[error("jsonL {0}")]
    Json(#[from] serde_json::Error),
    #[error("{0}")]
    Message(String),
}

impl Error {
    pub fn message(msg: impl Into<String>) -> Self {
        Self::Message(msg.into())
    }
}
