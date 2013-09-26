#include "pcheader.h"
#include "MHarrisBuffer.h"

bool MHarrisBuffer::Init(System::IntPtr image) {
  bool res = harrisbuffer_->Init(reinterpret_cast<IplImage*>(image.ToPointer()), "output.txt");
  return res;
}

void MHarrisBuffer::ProcessFrame(System::IntPtr image) {
  harrisbuffer_->ProcessFrame(reinterpret_cast<IplImage*>(image.ToPointer()), NULL, NULL);
}

void MHarrisBuffer::DrawInteresPoints(System::IntPtr image) {
  harrisbuffer_->DrawInterestPoints(reinterpret_cast<IplImage*>(image.ToPointer()));
}

System::Collections::ArrayList^ MHarrisBuffer::GetInterestPoints() {
  using System::Collections::ArrayList;
  ArrayList^ list = gcnew ArrayList();
  for (int i = 0; i < (int)harrisbuffer_->ipList.size(); i++) {
    InterestPoint& point = harrisbuffer_->ipList[i];
    if (!point.reject)
      list->Add(gcnew MInterestPoint(point.x, point.y, point.sx2));
  }
  return list;
}