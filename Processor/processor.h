#pragma once
#include "pcheader.h"
#include "feature_processor.h"
#include "infengine.h"

namespace handinput {
  // The main interface to the gesture recognition engine that handles feature extraction and 
  // gesture recognition.
  class PROCESSOR_API Processor {
  public:
    Processor(int w, int h, const std::string& model_file);
    // Returns
    // The most probable gesture label. Returns 0 if the gesture is unknown.
    std::string Update(float x, float y, float z, IplImage* image, IplImage* skin, 
                       bool visualize = false);
    void Reset() { inf_engine_->Reset(); };
    int KinectSampleRate() { return inf_engine_->kinect_sample_rate(); }
  private:
    std::unique_ptr<FeatureProcessor> feature_proc_;
    std::unique_ptr<InfEngine> inf_engine_;

  };
}